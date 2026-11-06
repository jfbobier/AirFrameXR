// An empty stand-in for UEVR's backend DLL (UEVRBackend.dll, built from the UEVR repository's `uevr` target): the lab loads it into xr_probe
// (XR_PROBE_LOAD_MODULE=UEVRBackend.dll) so the shim's fov_restriction_whitelist.txt check ("this module is loaded in the game's process") can be tested without Unreal Engine.
#include <windows.h>
BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
