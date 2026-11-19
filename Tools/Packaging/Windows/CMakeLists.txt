cmake_minimum_required(VERSION 3.21)
project(LiberaFxLauncher LANGUAGES CXX RC)

add_executable(LiberaFx WIN32 Launcher.cpp Launcher.rc)
target_compile_features(LiberaFx PRIVATE cxx_std_17)
target_include_directories(LiberaFx PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../../../Assets/Brand")
set_property(TARGET LiberaFx PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
