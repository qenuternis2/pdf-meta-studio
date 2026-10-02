# Сборка статической библиотеки Adobe XMP Core (без XMPFiles и без C++ DOM API)
# из исходников XMP-Toolkit-SDK вместе с Expat.

set(XMP_DIR "${PDFMETA_EXTERNAL_DIR}/xmp")
set(EXPAT_DIR "${PDFMETA_EXTERNAL_DIR}/expat/expat/lib")
if(NOT EXISTS "${XMP_DIR}/XMPCore/source/XMPMeta.cpp")
  message(FATAL_ERROR "XMP-Toolkit-SDK not found in ${XMP_DIR}. Run scripts/fetch-deps.")
endif()
if(NOT EXISTS "${EXPAT_DIR}/xmlparse.c")
  message(FATAL_ERROR "Expat not found in ${EXPAT_DIR}. Run scripts/fetch-deps.")
endif()

set(XC "${XMP_DIR}/XMPCore/source")

# Обязательный патч scripts/patches/xmp-keep-translations.patch: без него SDK при разборе
# и сериализации копирует x-default в единственный перевод (потеря данных).
file(READ "${XC}/XMPCore_Impl.cpp" _xmp_impl)
string(FIND "${_xmp_impl}" "array->children[1]->value = array->children[0]->value" _xmp_bug)
if(NOT _xmp_bug EQUAL -1)
  message(FATAL_ERROR "XMP-Toolkit-SDK не пропатчен. Запустите scripts/fetch-deps или примените scripts/patches/xmp-keep-translations.patch")
endif()
file(GLOB XMP_WRAPPERS "${XC}/WXMP*.cpp")
file(GLOB XMP_CORE_CLASSES "${XC}/XMP*.cpp")
list(REMOVE_ITEM XMP_CORE_CLASSES "${XC}/XMPCore_Impl.cpp")
file(GLOB XMP_ZUID "${XMP_DIR}/third-party/zuid/interfaces/*.cpp")

add_library(xmpcore_static STATIC
  ${XMP_WRAPPERS}
  ${XMP_CORE_CLASSES}
  ${XMP_ZUID}
  "${XC}/CoreObjectFactoryImpl.cpp"
  "${XC}/ExpatAdapter.cpp"
  "${XC}/ParseRDF.cpp"
  "${XC}/XMPCore_Impl.cpp"
  "${XMP_DIR}/source/UnicodeConversions.cpp"
  "${XMP_DIR}/source/XML_Node.cpp"
  "${XMP_DIR}/source/XMP_LibUtils.cpp"
  "${EXPAT_DIR}/xmlparse.c"
  "${EXPAT_DIR}/xmlrole.c"
  "${EXPAT_DIR}/xmltok.c"
)

if(MINGW)
  # Только проверочная кросс-сборка из Linux: ветка WIN_ENV в SDK рассчитана на MSVC
  # (SAL-аннотации, std::tr1), поэтому XMP Core собирается в POSIX-варианте поверх winpthreads.
  # Код самого worker при этом идёт по ветке _WIN32. Рабочая сборка для Windows — MSVC.
  set(XMP_PLATFORM_RES "${XMP_DIR}/XMPCore/resource/linux")
  set(XMP_ENV_DEFS UNIX_ENV=1 _POSIX_THREAD_SAFE_FUNCTIONS=1)
  set(XMP_MINGW_OPTS -include "${CMAKE_CURRENT_BINARY_DIR}/xmp_shim/mingw_sal.h")
elseif(WIN32)
  set(XMP_PLATFORM_RES "${XMP_DIR}/XMPCore/resource/win")
  set(XMP_ENV_DEFS WIN_ENV=1 WIN64=1 _WIN64=1 NOMINMAX UNICODE _UNICODE _CRT_SECURE_NO_WARNINGS)
else()
  set(XMP_PLATFORM_RES "${XMP_DIR}/XMPCore/resource/linux")
  set(XMP_ENV_DEFS UNIX_ENV=1)
  set(EXPAT_ENTROPY_DEFS HAVE_GETRANDOM=1)
endif()

# ExpatAdapter.cpp подключает "third-party/expat/lib/expat.h" относительно корня SDK;
# вместо копирования Expat в дерево SDK создаём заголовок-переходник.
set(XMP_SHIM_DIR "${CMAKE_CURRENT_BINARY_DIR}/xmp_shim")
file(WRITE "${XMP_SHIM_DIR}/third-party/expat/lib/expat.h" "#include \"${EXPAT_DIR}/expat.h\"\n")
if(MINGW)
  # Проверочная кросс-сборка MinGW: SDK пишет <Windows.h>, а заголовки MinGW в нижнем регистре.
  file(WRITE "${XMP_SHIM_DIR}/Windows.h" "#include <windows.h>\n")
  # SAL-аннотации MSVC отсутствуют в MinGW: берём пустые определения из самого SDK.
  file(READ "${XMP_DIR}/source/SuppressSAL.h" _sal)
  string(REPLACE "#if !defined(_WIN32) && !defined(_WIN64)" "#if 1" _sal "${_sal}")
  file(WRITE "${XMP_SHIM_DIR}/mingw_sal.h" "${_sal}")
endif()

target_include_directories(xmpcore_static
  PUBLIC "${XMP_DIR}/public/include"
  PRIVATE "${XMP_SHIM_DIR}" "${XMP_DIR}" "${EXPAT_DIR}" "${XMP_PLATFORM_RES}")

target_compile_definitions(xmpcore_static
  PUBLIC ${XMP_ENV_DEFS} XMP_StaticBuild=1 TXMP_STRING_TYPE=std::string
  PRIVATE
    BUILDING_XMPCORE_LIB=1 BUILDING_XMPCORE_AS_STATIC=1
    XMP_COMPONENT_INT_NAMESPACE=AdobeXMPCore_Int
    ENABLE_CPP_DOM_MODEL=0
    # Запрет DOCTYPE и любых сущностей в XMP (защита от XXE и «billion laughs»).
    BanAllEntityUsage=1
    ${EXPAT_ENTROPY_DEFS}
    XML_STATIC=1 HAVE_EXPAT_CONFIG_H=1 XML_GE=0 XML_CONTEXT_BYTES=1024
    $<$<CONFIG:Release>:NDEBUG=1>)

# Исходники Adobe не рассчитаны на строгие предупреждения.
if(MSVC)
  target_compile_options(xmpcore_static PRIVATE /W0 /EHsc /bigobj)
else()
  target_compile_options(xmpcore_static PRIVATE -w -fexceptions ${XMP_MINGW_OPTS}
    $<$<COMPILE_LANGUAGE:CXX>:-Wno-reorder>)
endif()
