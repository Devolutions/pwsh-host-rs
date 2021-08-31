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

if (${CMAKE_SYSTEM_PROCESSOR} STREQUAL "AMD64")
	# cmake reports AMD64 on Windows, but we might be building for 32-bit.
	if (CMAKE_CL_64)
		set(ARCH "x64")
	else()
		set(ARCH "x86")
	endif()
elseif (${CMAKE_SYSTEM_PROCESSOR} STREQUAL "x86")
	set(ARCH "x86")
elseif (${CMAKE_SYSTEM_PROCESSOR} STREQUAL "i386")
	set(ARCH "x86")
elseif (${CMAKE_SYSTEM_PROCESSOR} STREQUAL "i686")
	set(ARCH "x86")
elseif (${CMAKE_SYSTEM_PROCESSOR} STREQUAL "arm")
	set(ARCH "arm")
else()
	message(FATAL_ERROR "Unknown processor:" ${CMAKE_SYSTEM_PROCESSOR})
endif()

if (${ARCH} STREQUAL "x86" AND APPLE)
	# With CMake 2.8.x, ${CMAKE_SYSTEM_PROCESSOR} evalutes to i386 on OS X,
	# but clang defaults to 64-bit builds on OS X unless otherwise told.
	# Set ARCH to x86_64 so clang and CMake agree. This is fixed in CMake 3.
	set(ARCH "x64")
endif()

set(BINDINGS_DLL "${CMAKE_SOURCE_DIR}/bindings/bin/${ARCH}/Release/net5.0/Bindings.dll")

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

add_library(resources STATIC resources.c)

target_link_libraries(resources ${CMAKE_DL_LIBS} Threads::Threads)

export(PACKAGE pwsh-host)

install(DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/include"
	DESTINATION include
	FILES_MATCHING PATTERN "*.h")
