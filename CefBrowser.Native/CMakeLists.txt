cmake_minimum_required(VERSION 3.20)
project(CefBrowser.Native)

set(CEF_ROOT "" CACHE PATH "Path to extracted CEF binary distribution")
find_package(CEF REQUIRED)

add_subdirectory(${CEF_LIBCEF_DLL_WRAPPER_PATH} libcef_dll_wrapper)

# Imported target used by both platforms (CEF only sets CEF_LIB_* variables;
# CEF_LIB itself is not defined on Linux).
ADD_LOGICAL_TARGET("libcef_lib" "${CEF_LIB_DEBUG}" "${CEF_LIB_RELEASE}")

set(BROWSER_SRCS
    src/main.cpp
    src/browser_handler.cpp
    src/stdio_server.cpp
    src/protocol.cpp
)

SET_CEF_TARGET_OUT_DIR()

if(OS_WINDOWS)
  add_executable(CefBrowser.Native WIN32 ${BROWSER_SRCS})
elseif(OS_LINUX)
  add_executable(CefBrowser.Native ${BROWSER_SRCS})
else()
  message(FATAL_ERROR "CefBrowser.Native supports Windows and Linux only")
endif()

add_dependencies(CefBrowser.Native libcef_dll_wrapper)
SET_EXECUTABLE_TARGET_PROPERTIES(CefBrowser.Native)

if(OS_WINDOWS)
  # Sandbox support: cef_sandbox.lib implements cef_sandbox_info_create() and
  # requires its own set of system libraries (PSAPI_VERSION=1 comes from CEF's
  # compile definitions). Linking it unconditionally is safe even when runtime
  # sandbox is disabled, but it only exists when CEF was configured with
  # USE_SANDBOX=ON (the default).
  if(USE_SANDBOX)
    ADD_LOGICAL_TARGET("cef_sandbox_lib"
                       "${CEF_SANDBOX_LIB_DEBUG}"
                       "${CEF_SANDBOX_LIB_RELEASE}")
  endif()

  target_link_libraries(CefBrowser.Native PRIVATE
      libcef_lib
      libcef_dll_wrapper
      ${CEF_STANDARD_LIBS}
      d3d11.lib
      glu32.lib
      imm32.lib
      opengl32.lib
      # COM: IFileDialog (file inputs), SHGetFolderPath (downloads folder)
      ole32.lib
      shell32.lib
      # MessageBox / dialog window classes
      user32.lib
      gdi32.lib
      comdlg32.lib
  )

  if(USE_SANDBOX)
    target_link_libraries(CefBrowser.Native PRIVATE
        cef_sandbox_lib
        ${CEF_SANDBOX_STANDARD_LIBS}
    )
  endif()
elseif(OS_LINUX)
  find_package(PkgConfig REQUIRED)
  pkg_check_modules(GTK3 REQUIRED IMPORTED_TARGET gtk+-3.0)
  pkg_check_modules(X11 REQUIRED IMPORTED_TARGET x11)

  # libcef_dll_wrapper is static and references symbols from libcef.so, so it
  # must appear before libcef_lib on the linker command line.
  target_link_libraries(CefBrowser.Native PRIVATE
      libcef_dll_wrapper
      libcef_lib
      ${CEF_STANDARD_LIBS}
      PkgConfig::GTK3
      PkgConfig::X11
      ${CMAKE_DL_LIBS}
      pthread
  )
endif()

COPY_FILES(CefBrowser.Native "${CEF_BINARY_FILES}" "${CEF_BINARY_DIR}" "${CEF_TARGET_OUT_DIR}")
COPY_FILES(CefBrowser.Native "${CEF_RESOURCE_FILES}" "${CEF_RESOURCE_DIR}" "${CEF_TARGET_OUT_DIR}")
