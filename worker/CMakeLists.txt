cmake_minimum_required(VERSION 3.21)
project(pdfmeta_worker VERSION 0.1.0 LANGUAGES C CXX)

# Исходники зависимостей кладёт scripts/fetch-deps.(sh|ps1) в worker/external
# (версии закреплены в scripts/deps.lock). Можно указать другой каталог.
set(PDFMETA_EXTERNAL_DIR "${CMAKE_CURRENT_SOURCE_DIR}/external" CACHE PATH
    "Directory with qpdf/, xmp/ and expat/ source trees")
option(PDFMETA_BUILD_TESTS "Build worker self-tests" ON)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
if(MSVC)
  # Статическая CRT: EXE не требует отдельной установки VC++ Redistributable.
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

# --- qpdf ---------------------------------------------------------------
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(BUILD_STATIC_LIBS ON CACHE BOOL "" FORCE)
set(BUILD_DOC OFF CACHE BOOL "" FORCE)
set(USE_IMPLICIT_CRYPTO OFF CACHE BOOL "" FORCE)
set(REQUIRE_CRYPTO_NATIVE ON CACHE BOOL "" FORCE)
set(INSTALL_EXAMPLES OFF CACHE BOOL "" FORCE)
set(INSTALL_PKGCONFIG OFF CACHE BOOL "" FORCE)
set(INSTALL_CMAKE_PACKAGE OFF CACHE BOOL "" FORCE)
if(MSVC AND DEFINED VCPKG_TARGET_TRIPLET)
  # Библиотеки из vcpkg (x64-windows-static) берём явно из release-каталога: генератор Visual Studio
  # многоконфигурационный, и без этого qpdf может подхватить debug-версии (конфликт CRT, LIBCMTD).
  # Статическая zlib в vcpkg называется zs.lib, а qpdf ищет z/zlib. qpdf использует уже заполненный кэш.
  set(_vcpkg_rel "${VCPKG_INSTALLED_DIR}/${VCPKG_TARGET_TRIPLET}")
  find_path(ZLIB_H_PATH zlib.h PATHS "${_vcpkg_rel}/include" NO_DEFAULT_PATH)
  find_library(ZLIB_LIB_PATH NAMES zs zlib PATHS "${_vcpkg_rel}/lib" NO_DEFAULT_PATH)
  find_path(LIBJPEG_H_PATH jpeglib.h PATHS "${_vcpkg_rel}/include" NO_DEFAULT_PATH)
  find_library(LIBJPEG_LIB_PATH NAMES jpeg PATHS "${_vcpkg_rel}/lib" NO_DEFAULT_PATH)
endif()
add_subdirectory("${PDFMETA_EXTERNAL_DIR}/qpdf" qpdf EXCLUDE_FROM_ALL)

# --- Adobe XMP Core + Expat ----------------------------------------------
include(cmake/XMPCore.cmake)

# --- worker ---------------------------------------------------------------
add_library(pdfmeta_core STATIC
  src/protocol.cpp
  src/sha256.cpp
  src/xmp_model.cpp
  src/pdf_inspect.cpp
  src/pdf_edit.cpp
  src/fileutil.cpp
)
target_include_directories(pdfmeta_core PUBLIC src third_party)
target_link_libraries(pdfmeta_core PUBLIC libqpdf xmpcore_static)
target_compile_definitions(pdfmeta_core PUBLIC PDFMETA_VERSION="${PROJECT_VERSION}")
if(WIN32)
  target_compile_definitions(pdfmeta_core PUBLIC NOMINMAX WIN32_LEAN_AND_MEAN UNICODE _UNICODE)
endif()

add_executable(pdfmeta-worker src/main.cpp)
target_link_libraries(pdfmeta-worker PRIVATE pdfmeta_core)
if(MINGW)
  # Проверочная кросс-сборка из Linux: всё статически, без DLL MinGW рядом с EXE.
  target_link_options(pdfmeta-worker PRIVATE -static -static-libgcc -static-libstdc++)
endif()
if(UNIX)
  find_package(Threads REQUIRED)
  target_link_libraries(pdfmeta-worker PRIVATE Threads::Threads)
endif()

if(PDFMETA_BUILD_TESTS)
  # qpdf CLI (для создания зашифрованных PDF в тестах). Цель qpdf лежит в подпроекте с
  # EXCLUDE_FROM_ALL и не видна в решении Visual Studio верхнего уровня — собираем через test-tools.
  add_custom_target(test-tools)
  add_dependencies(test-tools qpdf)
  enable_testing()
  find_package(Python3 COMPONENTS Interpreter)
  if(Python3_FOUND)
    add_test(NAME worker_e2e
      COMMAND ${Python3_EXECUTABLE} ${CMAKE_CURRENT_SOURCE_DIR}/tests/run_tests.py
              $<TARGET_FILE:pdfmeta-worker>)
  endif()
endif()
