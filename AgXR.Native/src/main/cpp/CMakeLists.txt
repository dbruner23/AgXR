cmake_minimum_required(VERSION 3.22.1)

project("agxr_edge_vision")

# Define the library
add_library(
        agxr_vision
        SHARED
        vision_module.cpp)

# Find libraries
find_library(
        log-lib
        log)
        
# For ARCore (if linking directly)
# find_package(arengine_sdk REQUIRED) 

# Link libraries
target_link_libraries(
        agxr_vision
        ${log-lib}
        # arcore_sdk - Un-comment if linking ARCore NDK directly
)
