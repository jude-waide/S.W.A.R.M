cmake_minimum_required(VERSION 3.24)

project(DroneSwarmSkyrim VERSION 0.1.0 LANGUAGES CXX)

include(FetchContent)

set(BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(COMMONLIB_PREBUILT ON CACHE BOOL "" FORCE)
set(ENABLE_SKYRIM_SE ON CACHE BOOL "" FORCE)
set(ENABLE_SKYRIM_AE ON CACHE BOOL "" FORCE)
set(ENABLE_SKYRIM_VR ON CACHE BOOL "" FORCE)

FetchContent_Declare(
    CommonLibSSE
    GIT_REPOSITORY https://github.com/alandtse/CommonLibSSE-NG.git
    GIT_TAG v16.5.0
    GIT_SHALLOW TRUE
)
FetchContent_MakeAvailable(CommonLibSSE)
get_target_property(COMMONLIB_SOURCE_DIR CommonLibSSE SOURCE_DIR)
include("${COMMONLIB_SOURCE_DIR}/cmake/CommonLibSSE.cmake")

find_package(nlohmann_json CONFIG REQUIRED)

add_library(DroneSwarmSkyrim SHARED
    src/plugin.cpp
    src/plugin_info.cpp
)

target_compile_features(DroneSwarmSkyrim PRIVATE cxx_std_23)
target_compile_definitions(DroneSwarmSkyrim PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(DroneSwarmSkyrim
    PUBLIC CommonLibSSE::CommonLibSSE
    PRIVATE nlohmann_json::nlohmann_json winhttp ole32
)
target_precompile_headers(DroneSwarmSkyrim PRIVATE
    "${COMMONLIB_SOURCE_DIR}/include/SKSE/SKSE.h"
)

set_target_properties(DroneSwarmSkyrim PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/Data/SKSE/Plugins"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/Data/SKSE/Plugins"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/Data/SKSE/Plugins"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/Data/SKSE/Plugins"
)
