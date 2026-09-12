// BOOT (GPIO0) as a volume key. See boot_button.cpp for why one button carries both directions.
#pragma once

namespace settings_app {

void startBootButton();   // idempotent; called from the Settings app's install-time init()

} // namespace settings_app
