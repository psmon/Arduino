set(PROJ_SRC ${CMAKE_CURRENT_LIST_DIR})

# The Akka remoting client is not vendored here: these are the same sources the PC
# harness in project/samples/akka/cpp compiles and tests.
set(AKKA_ROOT ${PROJ_SRC}/../../../akka/cpp)

file(GLOB PROJ_SRCS_C   ${PROJ_SRC}/*.c ${PROJ_SRC}/assets/*.c)
file(GLOB PROJ_SRCS_CPP ${PROJ_SRC}/*.cpp)

set(AKKA_SRCS
    ${AKKA_ROOT}/src/pb.cpp
    ${AKKA_ROOT}/src/akka_wire.cpp
    ${AKKA_ROOT}/src/remote_client.cpp
    ${AKKA_ROOT}/src/tcp_stream.cpp
    ${AKKA_ROOT}/src/ima_adpcm.cpp
)

# Shares the NUS transport and the NanumGothic font with brookesia_app_claude_hud: the
# Akka PDUs are tunnelled over that BLE link, so this app needs no WiFi either.
idf_component_register(
    SRCS ${PROJ_SRCS_C} ${PROJ_SRCS_CPP} ${AKKA_SRCS}
    INCLUDE_DIRS ${PROJ_SRC} ${AKKA_ROOT}/include
    REQUIRES brookesia_core brookesia_app_claude_hud json bt esp_ringbuf
    WHOLE_ARCHIVE
)

set_source_files_properties(
    ${PROJ_SRCS_CPP}
    PROPERTIES
        COMPILE_FLAGS "-Wno-missing-field-initializers"
)
