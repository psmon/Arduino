// Single inbound transport: BLE Nordic UART Service. Feeds State::handleLine(), never touches LVGL.
// If BLE fails to start, a warning is logged and the HUD simply stays empty (no fallback transports).
#pragma once
namespace claude_hud {
void startBle();        // advertise as "claude-hud"; RX writes of "S {json}" / "E {json}"
}
