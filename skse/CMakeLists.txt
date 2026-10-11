cmake_minimum_required(VERSION 3.21)

file(READ "${CMAKE_CURRENT_SOURCE_DIR}/../VERSION" ANVILLOD_VERSION)
string(STRIP "${ANVILLOD_VERSION}" ANVILLOD_VERSION)
project(AnvilLOD VERSION ${ANVILLOD_VERSION} LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_INTERPROCEDURAL_OPTIMIZATION ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")

set(ANVILLOD_RUNTIME_LINE "1" CACHE STRING "Build line: 1 (SE 1.5.97 - AE 1.6.1170), 17 (newer than 1.6.1170) or 14 (Skyrim VR 1.4.15)")

find_package(CommonLibSSE CONFIG REQUIRED)
find_package(spdlog CONFIG REQUIRED)
if(ANVILLOD_RUNTIME_LINE STREQUAL "17" OR ANVILLOD_RUNTIME_LINE STREQUAL "14")
    # CommonLibSSE-NG 7.2.0 links Microsoft::DirectXTK in its interface without finding it itself,
    # so the plugin has to, or the generate step fails.
    find_package(directxtk CONFIG REQUIRED)
endif()

# Two build lines from one source tree (like ApocryphaRealm Menu Framework):
#   1  = Skyrim SE 1.5.97 / AE 1.6.x, CommonLibSSE-NG from the colorglass registry (root vcpkg.json)
#   17 = Skyrim 1.7.x, CommonLibSSE-NG 7.2.0 (alandtse ng), which reads Address Library's format-5 databases
#        (cmake/manifest-17 + cmake/ports-17, preset release-17)
#   14 = Skyrim VR 1.4.15, the same CommonLibSSE-NG 7.2.0 built for VR only
#        (cmake/manifest-vr + cmake/ports-vr, preset release-vr)
add_commonlibsse_plugin(${PROJECT_NAME}
    AUTHOR "CageTV"
    SOURCES
        src/main.cpp
        src/DataFile.cpp
        src/Controller.cpp
        src/Settings.cpp
        src/LodDistances.cpp
        src/IniFile.cpp
        src/Menu.cpp
        src/LodLayers.cpp
)

if(ANVILLOD_RUNTIME_LINE STREQUAL "14")
    # The VR build of CommonLib pulls in <openvr.h> (installed by cmake/ports-vr); link its import library too.
    find_library(OPENVR_API_LIB openvr_api REQUIRED)
    target_link_libraries(${PROJECT_NAME} PRIVATE ${OPENVR_API_LIB})
endif()

target_compile_features(${PROJECT_NAME} PRIVATE cxx_std_23)
target_compile_definitions(${PROJECT_NAME} PRIVATE ANVILLOD_RUNTIME_LINE=${ANVILLOD_RUNTIME_LINE} ANVILLOD_VERSION_STRING="${ANVILLOD_VERSION}" NOMINMAX WIN32_LEAN_AND_MEAN)
target_precompile_headers(${PROJECT_NAME} PRIVATE src/PCH.h)
target_include_directories(${PROJECT_NAME} PRIVATE src)
target_compile_options(${PROJECT_NAME} PRIVATE /W4 /permissive- /Zc:preprocessor /utf-8)
