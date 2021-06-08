cmake_minimum_required(VERSION 3.2)

project(native-host C CXX)

set(CMAKE_C_STANDARD 99)
set(CMAKE_CXX_STANDARD 11)

include(cmake/yarc.cmake)

find_program(DOTNET_EXECUTABLE dotnet REQUIRED)
find_program(PWSH_EXECUTABLE pwsh REQUIRED)

set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_CURRENT_BINARY_DIR}/bin)
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_CURRENT_BINARY_DIR}/lib)
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${CMAKE_CURRENT_BINARY_DIR}/lib)

set(THREADS_PREFER_PTHREAD_FLAG ON)
find_package(Threads REQUIRED)

if(WIN32)
	set(C_FLAGS "")
	set(C_FLAGS "${C_FLAGS} -D_UNICODE")
	set(C_FLAGS "${C_FLAGS} -D_CRT_SECURE_NO_WARNINGS")
	set(C_FLAGS "${C_FLAGS} -DWIN32_LEAN_AND_MEAN")
	set(C_FLAGS "${C_FLAGS} -D_WINSOCK_DEPRECATED_NO_WARNINGS")
	set(C_FLAGS "${C_FLAGS} -DWINVER=0x0601 -D_WIN32_WINNT=0x0601")
	set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} ${C_FLAGS}")
	set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} ${C_FLAGS}")
endif()

if(CMAKE_COMPILER_IS_GNUCC)
	set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -Wformat-truncation=0")
endif()

set(DOTNET_ARGS "build"
	"${CMAKE_SOURCE_DIR}/bindings"
	"-c"
	"Release")

set(BINDINGS_DLL "${CMAKE_SOURCE_DIR}/bindings/bin/Release/net5.0/Bindings.dll")

add_custom_command(COMMAND ${DOTNET_EXECUTABLE}
	ARGS ${DOTNET_ARGS}
	WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}/bindings"
	OUTPUT ${BINDINGS_DLL}
	DEPENDS "${CMAKE_SOURCE_DIR}/bindings/Bindings.cs")

yarc_bundle(NAME bindings
	RESOURCES ${BINDINGS_DLL}
	OUTPUT resources.c)

add_definitions("-DPWSH_HOST_EXPORTS")

include_directories(include)

add_library(pwsh-host STATIC
	resources.c
	pwsh-host.c)

target_link_libraries(pwsh-host
	${CMAKE_DL_LIBS} Threads::Threads)

export(PACKAGE pwsh-host)

install(DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/include"
	DESTINATION include
	FILES_MATCHING PATTERN "*.h")
