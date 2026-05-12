cmake_minimum_required(VERSION 3.20)
project(CefBrowser.Native)

set(CEF_ROOT "" CACHE PATH "Path to extracted CEF binary distribution")
find_package(CEF REQUIRED)

add_subdirectory(${CEF_LIBCEF_DLL_WRAPPER_PATH} libcef_dll_wrapper)

if(OS_WINDOWS)
  ADD_LOGICAL_TARGET("libcef_lib" "${CEF_LIB_DEBUG}" "${CEF_LIB_RELEASE}")
endif()

set(BROWSER_SRCS
    src/main.cpp
    src/browser_handler.cpp
    src/pipe_server.cpp
)

SET_CEF_TARGET_OUT_DIR()

add_executable(CefBrowser.Native WIN32 ${BROWSER_SRCS})

add_dependencies(CefBrowser.Native libcef_dll_wrapper)
SET_EXECUTABLE_TARGET_PROPERTIES(CefBrowser.Native)
target_link_libraries(CefBrowser.Native PRIVATE
    libcef_lib
    libcef_dll_wrapper
    ${CEF_STANDARD_LIBS}
    d3d11.lib
    glu32.lib
    imm32.lib
    opengl32.lib
)

COPY_FILES(CefBrowser.Native "${CEF_BINARY_FILES}" "${CEF_BINARY_DIR}" "${CEF_TARGET_OUT_DIR}")
COPY_FILES(CefBrowser.Native "${CEF_RESOURCE_FILES}" "${CEF_RESOURCE_DIR}" "${CEF_TARGET_OUT_DIR}")
