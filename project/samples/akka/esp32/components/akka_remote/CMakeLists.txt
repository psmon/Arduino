# ESP-IDF component wrapper around the portable sources in ../../../cpp.
#
# There is no second copy of the protocol code: the files the dev-box harness
# compiles are the files that go on the board. Only the byte stream differs, and
# tcp_stream.cpp covers both (winsock vs lwIP) behind one #ifdef.
set(AKKA_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../../cpp")

idf_component_register(
    SRCS
        "${AKKA_ROOT}/src/pb.cpp"
        "${AKKA_ROOT}/src/akka_wire.cpp"
        "${AKKA_ROOT}/src/remote_client.cpp"
        "${AKKA_ROOT}/src/tcp_stream.cpp"
    INCLUDE_DIRS "${AKKA_ROOT}/include"
    REQUIRES lwip
)

target_compile_options(${COMPONENT_LIB} PRIVATE -Wall -Wextra)
