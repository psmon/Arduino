idf_component_register(
    SRCS "main.cpp"
    INCLUDE_DIRS "."
    REQUIRES akka_remote esp_wifi esp_netif nvs_flash esp_event
)
