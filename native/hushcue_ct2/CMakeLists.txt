cmake_minimum_required(VERSION 3.20)
project(hushcue_ct2 LANGUAGES CXX)

# Built against the prebuilt ctranslate2.dll from the PyPI wheel (the same binary faster-whisper uses),
# through an import library generated from its exports. See native/build.ps1.
set(CT2_INCLUDE_DIR "" CACHE PATH "CTranslate2 include directory (matching the DLL's version)")
set(CT2_IMPORT_LIB "" CACHE FILEPATH "Import library for ctranslate2.dll")

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Same CRT as ctranslate2.dll: release, dynamic. Standard library types cross the DLL boundary.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreadedDLL")

add_library(hushcue_ct2 SHARED hushcue_ct2.cpp hushcue_ct2.h)
target_include_directories(hushcue_ct2 PRIVATE "${CT2_INCLUDE_DIR}")
target_link_libraries(hushcue_ct2 PRIVATE "${CT2_IMPORT_LIB}")
target_compile_definitions(hushcue_ct2 PRIVATE NOMINMAX _USE_MATH_DEFINES)
target_compile_options(hushcue_ct2 PRIVATE /EHsc /W3 /permissive- /Zc:__cplusplus /utf-8)
