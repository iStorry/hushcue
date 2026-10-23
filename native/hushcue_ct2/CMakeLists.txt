cmake_minimum_required(VERSION 3.20)
project(hushcue_ct2 LANGUAGES CXX)

# Built against the prebuilt CTranslate2 library from the PyPI wheel (the same binary faster-whisper uses): on Windows
# through an import library generated from ctranslate2.dll's exports (native/build.ps1), on macOS against the wheel's
# libctranslate2 dylib, copied next to the bridge (native/build.sh).
set(CT2_INCLUDE_DIR "" CACHE PATH "CTranslate2 include directory (matching the library's version)")
set(CT2_LIBRARY "" CACHE FILEPATH "Import library for ctranslate2.dll (Windows) or the libctranslate2 dylib (macOS)")

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Same CRT as ctranslate2.dll: release, dynamic. Standard library types cross the DLL boundary.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreadedDLL")

add_library(hushcue_ct2 SHARED hushcue_ct2.cpp hushcue_ct2.h)
target_include_directories(hushcue_ct2 PRIVATE "${CT2_INCLUDE_DIR}")
target_link_libraries(hushcue_ct2 PRIVATE "${CT2_LIBRARY}")
target_compile_definitions(hushcue_ct2 PRIVATE _USE_MATH_DEFINES)

if(MSVC)
  target_compile_definitions(hushcue_ct2 PRIVATE NOMINMAX)
  target_compile_options(hushcue_ct2 PRIVATE /EHsc /W3 /permissive- /Zc:__cplusplus /utf-8)
else()
  set_target_properties(hushcue_ct2 PROPERTIES CXX_VISIBILITY_PRESET hidden VISIBILITY_INLINES_HIDDEN ON)
  target_compile_options(hushcue_ct2 PRIVATE -Wall -Wextra)
endif()
