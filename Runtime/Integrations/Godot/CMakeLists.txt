cmake_minimum_required(VERSION 3.22)
project(LibraFxGodot LANGUAGES CXX)

if(NOT IS_DIRECTORY "${GODOT_CPP_DIR}/gdextension")
    message(FATAL_ERROR "Set GODOT_CPP_DIR to a godot-cpp checkout with Godot 4.7 API support.")
endif()

set(GODOTCPP_API_VERSION "4.7")
set(GODOTCPP_BUILD_PROFILE "${CMAKE_CURRENT_SOURCE_DIR}/build_profile.json")
add_subdirectory("${GODOT_CPP_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/godot-cpp")

set(LIBERAFX_BUILD_INTEROP OFF)
set(LIBERAFX_BUILD_TESTS OFF)
add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/../.." "${CMAKE_CURRENT_BINARY_DIR}/core")
# LibraRuntimeCore includes all five Core/CpuParticles sources via CpuParticles.cmake.
# The renderer uses SpawnableHost directly and the static Flexible Plane C ABI.

add_library(liberafx_godot SHARED LibraFxGodot.cpp LibraFxGodotRender.cpp
    LibraFxGodotRenderSubmission.cpp LibraFxGodotGpu.cpp
    LibraFxGodotFlexiblePlane.cpp LibraFxGodotFluid2D.cpp)
target_link_libraries(liberafx_godot PRIVATE godot::cpp LibraRuntimeCore)
target_compile_features(liberafx_godot PRIVATE cxx_std_17)
option(LIBERAFX_GODOT_TEST_HOOKS "Compile isolated Fluid2D GPU verification hooks" OFF)
if(LIBERAFX_GODOT_TEST_HOOKS)
    target_compile_definitions(liberafx_godot PRIVATE LIBERAFX_GODOT_TEST_HOOKS=1)
endif()
if(LIBERAFX_PROJECT_NODE_SOURCES)
    target_sources(liberafx_godot PRIVATE ${LIBERAFX_PROJECT_NODE_SOURCES})
    target_compile_definitions(liberafx_godot PRIVATE LIBERAFX_HAS_PROJECT_NODES=1)
endif()
set_target_properties(liberafx_godot PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
