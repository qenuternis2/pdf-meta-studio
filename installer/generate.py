"""Generate a per-user WiX package with stable components for every published file."""
import argparse
import pathlib
import uuid
import xml.etree.ElementTree as ET

NS = 'http://wixtoolset.org/schemas/v4/wxs'
ET.register_namespace('', NS)
def element(parent, name, **attributes):
    return ET.SubElement(parent, '{' + NS + '}' + name, attributes)
def identifier(kind, path):
    return kind + uuid.uuid5(uuid.NAMESPACE_URL, 'pdf-meta-studio/' + path).hex

parser = argparse.ArgumentParser()
parser.add_argument('publish', type=pathlib.Path)
parser.add_argument('output', type=pathlib.Path)
parser.add_argument('--version', required=True)
args = parser.parse_args()
source = args.publish.resolve()
files = sorted(path for path in source.rglob('*') if path.is_file())
if not (source / 'PdfMetaStudio.exe').is_file() or not (source / 'pdfmeta-worker.exe').is_file():
    parser.error('Publish the application and worker before generating the installer.')
if not (source / 'licenses' / 'MANIFEST.txt').is_file():
    parser.error('The mandatory third-party license bundle is missing.')
wix = ET.Element('{' + NS + '}Wix')
package = element(wix, 'Package', Name='PDF Meta Studio', Manufacturer='PDF Meta Studio', Version=args.version,
                  UpgradeCode='CA630B1F-9153-5830-A5EC-84A95D16534D', Scope='perUser', Language='1033')
element(package, 'MajorUpgrade', DowngradeErrorMessage='A newer version of PDF Meta Studio is already installed.')
element(package, 'MediaTemplate', EmbedCab='yes')
element(package, 'Property', Id='ARPNOREPAIR', Value='1')
element(package, 'Property', Id='ARPCOMMENTS', Value='Local PDF metadata editor. No administrator privileges required.')
local = element(package, 'StandardDirectory', Id='LocalAppDataFolder')
install = element(local, 'Directory', Id='INSTALLFOLDER', Name='PdfMetaStudio')
feature = element(package, 'Feature', Id='Application', Title='PDF Meta Studio', Level='1')
directories = {'.': install}
for path in files:
    relative = path.relative_to(source)
    directory = pathlib.PurePath('.')
    for segment in relative.parts[:-1]:
        parent = directories[str(directory)]
        directory /= segment
        if str(directory) not in directories:
            directories[str(directory)] = element(parent, 'Directory', Id=identifier('D', directory.as_posix()), Name=segment)
    key = relative.as_posix()
    component_id = identifier('C', key)
    component = element(directories[str(directory)], 'Component', Id=component_id,
                        Guid=str(uuid.uuid5(uuid.NAMESPACE_URL, 'pdf-meta-studio/component/' + key)).upper())
    element(component, 'File', Id='MainExecutable' if key == 'PdfMetaStudio.exe' else identifier('F', key), Source=str(path))
    element(component, 'RegistryValue', Root='HKCU', Key='Software\\PdfMetaStudio\\InstallerFiles',
            Name=component_id, Type='integer', Value='1', KeyPath='yes')
    element(feature, 'ComponentRef', Id=component_id)
# Remove empty install directories on uninstall without touching user documents.
for directory, node in directories.items():
    component_id = identifier('Cleanup', directory)
    component = element(node, 'Component', Id=component_id, Guid=str(uuid.uuid5(uuid.NAMESPACE_URL, 'pdf-meta-studio/cleanup/' + directory)).upper())
    element(component, 'RemoveFolder', Id=identifier('Remove', directory), On='uninstall')
    element(component, 'RegistryValue', Root='HKCU', Key='Software\\PdfMetaStudio\\InstallerFolders', Name=component_id, Type='integer', Value='1', KeyPath='yes')
    element(feature, 'ComponentRef', Id=component_id)
menu = element(package, 'StandardDirectory', Id='ProgramMenuFolder')
folder = element(menu, 'Directory', Id='ApplicationMenu', Name='PDF Meta Studio')
component = element(folder, 'Component', Id='StartMenuShortcut', Guid='F58116E7-4882-566E-83B3-6C778A3A79EB')
element(component, 'Shortcut', Id='ApplicationShortcut', Name='PDF Meta Studio', Target='[#MainExecutable]', WorkingDirectory='INSTALLFOLDER')
element(component, 'RemoveFolder', Id='RemoveStartMenu', On='uninstall')
element(component, 'RegistryValue', Root='HKCU', Key='Software\\PdfMetaStudio', Name='Installed', Type='integer', Value='1', KeyPath='yes')
element(feature, 'ComponentRef', Id='StartMenuShortcut')
args.output.parent.mkdir(parents=True, exist_ok=True)
ET.indent(wix)
ET.ElementTree(wix).write(args.output, encoding='utf-8', xml_declaration=True)
