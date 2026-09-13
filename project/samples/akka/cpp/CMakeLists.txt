cmake_minimum_required(VERSION 3.16)
project(askbot_akka_remote CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# The protocol sources are deliberately plain C++17 with no ESP-IDF or Windows
# dependency, so the identical files build here and inside the ESP-IDF component.
add_library(akka_remote STATIC
    src/pb.cpp
    src/akka_wire.cpp
    src/remote_client.cpp
    src/tcp_stream.cpp
)
target_include_directories(akka_remote PUBLIC include)

if(WIN32)
    target_link_libraries(akka_remote PUBLIC ws2_32)
endif()

if(MSVC)
    # /utf-8: the sources carry UTF-8 literals and the default code page here is 949.
    add_compile_options(/utf-8 /W4)
else()
    target_compile_options(akka_remote PRIVATE -Wall -Wextra)
endif()

add_executable(askbot_cli tools/askbot_cli.cpp)
target_link_libraries(askbot_cli PRIVATE akka_remote)

add_executable(askbot_chat tools/askbot_chat.cpp)
target_link_libraries(askbot_chat PRIVATE akka_remote)

add_executable(akka_wire_test tests/akka_wire_test.cpp)
target_link_libraries(akka_wire_test PRIVATE akka_remote)

enable_testing()
add_test(NAME akka_wire COMMAND akka_wire_test)
