// MIT License
//
// Copyright(c) 2025-2026 Matthieu Bucchianeri
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this softwareand associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and /or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

#include "pch.h"

#include "constantsbuffer.h"
#include "log.h"
#include "driver.h"
#include "utils.h"

#include "ReprojectVS.h"
#include "ReprojectPS.h"
#include "LayerVS.h"
#include "LayerPS.h"
#include "LayerCubePS.h"
#include "layerconstants.h"
#include "ipc_win.h"

#include <algorithm>
#include <deque>
#include <mmdeviceapi.h>

static double EnvDouble(const char* n, double def) { char* v = nullptr; size_t len = 0; double r = def; if (_dupenv_s(&v, &len, n) == 0 && v) { r = atof(v); } free(v); return r; }
static bool EnvSet(const char* n) { char* v = nullptr; size_t len = 0; const bool set = _dupenv_s(&v, &len, n) == 0 && v; free(v); return set; }


using namespace visionalvr_ipc;

// Logging. General log: <folder of this DLL>\logs\VisionALVR.log, appended by every VisionALVR component (one WriteFile per
// line, so the host and several games can share it): game start/exit and errors only. Debug log (verbose): OVRSHIM_LOG = path
// (harness), else shim_<exe>_<pid>.log in the host's debug session folder, which the shim learns through the IPC block; lines
// logged before that are kept in memory and written once the folder is known.
// Compile-time telemetry flag: the per-frame trace (shim_frames.csv), the pose-match diagnostics and the bookkeeping behind them cost CPU
// on every frame. Release builds define VISIONALVR_RELEASE (tools/remote/stage11_ovrshim.ps1 via CL, run.py build --release) and drop them.
#ifndef VISIONALVR_RELEASE
#define VISIONALVR_TELEMETRY 1
#endif

namespace shimlog {
    static std::mutex g_mutex;
    static FILE* g_env = nullptr;     // OVRSHIM_LOG (harness / manual debugging)
    static FILE* g_session = nullptr; // shim_<exe>_<pid>.log in the host's debug session folder
    static std::vector<std::string> g_pending;
    static std::wstring g_debugDir;
    static bool g_dirKnown = false;   // the host told us (debug on with a folder, or off)

    static std::string Now() {
        SYSTEMTIME t;
        GetLocalTime(&t);
        char b[32];
        snprintf(b, sizeof(b), "%04d-%02d-%02d %02d:%02d:%02d.%03d", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
        return b;
    }

    static std::wstring ExePath() {
        wchar_t p[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, p, MAX_PATH);
        return p;
    }

    static std::wstring ExeName() {
        const std::wstring p = ExePath();
        const size_t k = p.find_last_of(L"\\/");
        return k == std::wstring::npos ? p : p.substr(k + 1);
    }

    static std::string Utf8(const std::wstring& w) {
        char b[512];
        const int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, b, sizeof(b), nullptr, nullptr);
        return n > 0 ? std::string(b) : std::string();
    }

    static std::wstring DllDir() {
        HMODULE m = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)&DllDir, &m);
        wchar_t p[MAX_PATH] = {};
        GetModuleFileNameW(m, p, MAX_PATH);
        std::wstring d = p;
        const size_t k = d.find_last_of(L"\\/");
        return k == std::wstring::npos ? L"." : d.substr(0, k);
    }

    static std::string Format(const char* fmt, va_list a) {
        char b[2048];
        vsnprintf(b, sizeof(b), fmt, a);
        return b;
    }

    static void General(const char* level, const char* fmt, ...) {
        va_list a;
        va_start(a, fmt);
        const std::string msg = Format(fmt, a);
        va_end(a);
        static const std::wstring path = [] {
            const std::wstring logs = DllDir() + L"\\logs";
            CreateDirectoryW(logs.c_str(), nullptr);
            return logs + L"\\VisionALVR.log";
        }();
        static const std::string who = "[shim:" + Utf8(ExeName()) + "]";
        std::string lv = level;
        if (lv.size() < 5) {
            lv.append(5 - lv.size(), ' ');
        }
        const std::string line = Now() + " " + who + " " + lv + " " + msg + "\r\n";
        HANDLE h = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS,
                               FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h != INVALID_HANDLE_VALUE) {
            DWORD w = 0;
            WriteFile(h, line.data(), (DWORD)line.size(), &w, nullptr);
            CloseHandle(h);
        }
    }

    static void InitEnvLocked() {
        static bool done = false;
        if (done) {
            return;
        }
        done = true;
        char path[MAX_PATH];
        const DWORD n = GetEnvironmentVariableA("OVRSHIM_LOG", path, MAX_PATH);
        if (n > 0 && n < MAX_PATH && strcmp(path, "0") != 0) {
            fopen_s(&g_env, path, "a");
        }
    }

    // Opens the debug log in the host's debug session folder (empty: debug off).
    static void SetDebugDir(const std::wstring& dir) {
        std::lock_guard lock(g_mutex);
        InitEnvLocked();
        if (g_dirKnown && dir == g_debugDir) {
            return;
        }
        g_dirKnown = true;
        g_debugDir = dir;
        if (g_session) {
            fclose(g_session);
            g_session = nullptr;
        }
        if (!dir.empty()) {
            wchar_t name[64];
            swprintf_s(name, L"_%lu.log", GetCurrentProcessId());
            std::wstring exe = ExeName();
            const size_t dot = exe.find_last_of(L'.');
            if (dot != std::wstring::npos) {
                exe = exe.substr(0, dot);
            }
            _wfopen_s(&g_session, (dir + L"\\shim_" + exe + name).c_str(), L"a");
        }
        if (g_session) {
            for (const auto& l : g_pending) {
                fputs(l.c_str(), g_session);
            }
            fflush(g_session);
        }
        g_pending.clear();
    }

    static void Debug(const char* fmt, ...) {
        std::lock_guard lock(g_mutex);
        InitEnvLocked();
        va_list a;
        va_start(a, fmt);
        char pid[16];
        snprintf(pid, sizeof(pid), " [%lu] ", GetCurrentProcessId());
        const std::string line = Now() + pid + Format(fmt, a) + "\n";
        va_end(a);
        if (g_env) {
            fputs(line.c_str(), g_env);
            fflush(g_env);
        }
        if (g_session) {
            fputs(line.c_str(), g_session);
            fflush(g_session);
        } else if (!g_dirKnown && g_pending.size() < 200) {
            g_pending.push_back(line); // until the host says whether debug is on
        }
    }

    // Per-frame trace (follow-the-frames harness): one CSV line per published frame, in the debug session folder
    // (shim_frames_<exe>_<pid>.csv) or at OVRSHIM_FRAME_CSV. Times are shim clock seconds.
    static void FrameCsv(const char* line) {
#ifdef VISIONALVR_TELEMETRY
        static FILE* f = nullptr;
        static bool tried = false;
        static int pending = 0;
        std::lock_guard lock(g_mutex);
        if (!f && !tried) {
            char path[MAX_PATH];
            const DWORD n = GetEnvironmentVariableA("OVRSHIM_FRAME_CSV", path, MAX_PATH);
            if (n > 0 && n < MAX_PATH) {
                fopen_s(&f, path, "w");
                tried = true;
            } else if (g_dirKnown && !g_debugDir.empty()) {
                wchar_t name[64];
                swprintf_s(name, L"_%lu.csv", GetCurrentProcessId());
                std::wstring exe = ExeName();
                const size_t dot = exe.find_last_of(L'.');
                if (dot != std::wstring::npos) {
                    exe = exe.substr(0, dot);
                }
                _wfopen_s(&f, (g_debugDir + L"\\shim_frames_" + exe + name).c_str(), L"w");
                tried = true;
            }
            if (f) {
                fputs("counter,slot,client_ts,display_time,t_release,t_read,t_sample_arrive,predicted,repeated,t_submit,t_compose_start,t_publish,qx,qy,qz,qw,sqx,sqy,sqz,sqw,spx,spy,spz,lpx,lpy,lpz\n", f);
            }
        }
        if (f) {
            fputs(line, f);
            if (++pending >= 90) {
                fflush(f);
                pending = 0;
            }
        }
#else
        (void)line;
#endif
    }
} // namespace shimlog

#define ShimLog shimlog::Debug

// what the frame trace records about a frame, gathered at submit and completed at publish
struct FrameTraceRow {
    double displayTime{0}, tRead{0}, tArrive{0}, tSubmit{0};
    int predicted{0}, repeated{0};
    float q[4]{0, 0, 0, 1};
    float sq[4]{0, 0, 0, 1}, sp[3]{0, 0, 0}, lp[3]{0, 0, 0}; // the host sample the game read (orientation, position) and the layer's eye-0 position
};

// VDXR's own extension flags (virtualdesktop-openxr/OVR_Ext.h): layer shown to one eye only.
static constexpr int kLayerFlagDisableLeft = 0x100000;
static constexpr int kLayerFlagDisableRight = 0x200000;

using namespace ovrnull::driver;
using namespace ovrnull::log;
using namespace ovrnull::utils;

namespace {

    class HostDriver : public IDriver {
      private:
        static inline const OVR::Posef k_HeadToLeftController = {{0, 0, 0, 1}, {-0.15f, -0.2f, -0.35f}};
        static inline const OVR::Posef k_HeadToRightController = {{0, 0, 0, 1}, {0.15f, -0.2f, -0.35f}};

        static constexpr size_t k_SwapchainLength = 3;
        struct Swapchain {
            ComPtr<ID3D11Texture2D> textures[k_SwapchainLength];
            ComPtr<ID3D11ShaderResourceView> SRVs[k_SwapchainLength];
            ComPtr<ID3D11RenderTargetView> RTVs[k_SwapchainLength];
            ovrTextureSwapChainDesc desc{};
            uint32_t lastCommittedIndex{0};
            // The slot holding the frame the app just committed. GetSwapchainImageIndex reports lastCommittedIndex - 1 as the slot to write
            // next, so VDXR commits until lastCommittedIndex - 2 is the slot it rendered/copied frame F into: that is the slot to read (reading
            // lastCommittedIndex itself composed frame F-1's image under frame F's pose).
            uint32_t ComposeIndex() const {
                return desc.StaticImage ? 0u : (uint32_t)((lastCommittedIndex + k_SwapchainLength - 2) % k_SwapchainLength);
            }
        };

      public:
        HostDriver() {
            TraceLocalActivity(local);
            TraceLoggingWriteStart(local, "HostDriver_Ctor");

            {
                ComPtr<IDXGIFactory6> factory;
                CreateDXGIFactory2(0, IID_PPV_ARGS(factory.ReleaseAndGetAddressOf()));
                ComPtr<IDXGIAdapter1> adapter;
                factory->EnumAdapterByGpuPreference(
                    0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(adapter.ReleaseAndGetAddressOf()));
                DXGI_ADAPTER_DESC1 desc;
                adapter->GetDesc1(&desc);
                m_adapterLuid = desc.AdapterLuid;
            }
            OpenHost();
            if (m_ipc.state && (m_ipc.state->host.adapterLuidLow || m_ipc.state->host.adapterLuidHigh)) {
                m_adapterLuid.LowPart = m_ipc.state->host.adapterLuidLow;
                m_adapterLuid.HighPart = m_ipc.state->host.adapterLuidHigh;
            }
            m_displayRate = 72.f;
            m_photonsTime = 1.f / m_displayRate;
            m_eyePose[ovrEye_Left].Position.x = -0.0315f;
            m_eyePose[ovrEye_Right].Position.x = 0.0315f;
            m_eyeFov[ovrEye_Left].UpTan = m_eyeFov[ovrEye_Left].DownTan = m_eyeFov[ovrEye_Left].LeftTan =
                m_eyeFov[ovrEye_Left].RightTan = (float)M_PI_2;
            m_eyeFov[ovrEye_Right] = m_eyeFov[ovrEye_Left];
            m_recommendedResolution = {2496, 2688};
            LoadHostConfig();
            m_controllerButtons.ControllerType = ovrControllerType_Touch;
            m_controllerPose[0].ThePose = OVR::Posef(m_hmdPose.ThePose) * k_HeadToLeftController;
            m_controllerPose[1].ThePose = OVR::Posef(m_hmdPose.ThePose) * k_HeadToRightController;
            m_controllerSides = 0x3;

            LARGE_INTEGER now{};
            QueryPerformanceCounter(&now);
            m_nextFramePredictedDisplayTime = QpcToOvrTime(now);

            TraceLoggingWriteStop(local, "HostDriver_Ctor");
        }

        ~HostDriver() override {
            TraceLocalActivity(local);
            TraceLoggingWriteStart(local, "HostDriver_Dtor");

            if (m_serverThread.joinable()) {
                m_terminateServerThread = true;
                m_serverThread.join();
                m_serverThread = {};
            }
            StopPublisher();

            while (!m_swapchains.empty()) {
                Swapchain* swapchain = (Swapchain*)*m_swapchains.begin();
                TraceLoggingWriteTagged(local, "HostDriver_Dtor", TLPArg(swapchain, "DeleteSwapchain"));
                delete swapchain;
                m_swapchains.erase(m_swapchains.begin());
            }

            if (m_ipc.state) {
                m_ipc.state->shim.shimAlive = 0;
                shimlog::General("INFO", "game exit: %llu frames", (unsigned long long)m_framesSubmitted);
            }
            visionalvr_ipc::CloseIpc(m_ipc);

            TraceLoggingWriteStop(local, "HostDriver_Dtor");
        }

        void SetSubmissionDevice(ID3D11Device* device) override {
            TraceLocalActivity(local);
            TraceLoggingWriteStart(local, "HostDriver_SetSubmissionDevice", TLPArg(device, "Device"));

            if (!m_submissionDevice) {
                m_submissionDevice = device;
                m_submissionDevice->GetImmediateContext(m_submissionContext.ReleaseAndGetAddressOf());

                winrt::check_hresult(m_submissionDevice->CreateVertexShader(
                    k_ReprojectVS, sizeof(k_ReprojectVS), nullptr, m_reprojectVS.ReleaseAndGetAddressOf()));
                winrt::check_hresult(m_submissionDevice->CreatePixelShader(
                    k_ReprojectPS, sizeof(k_ReprojectPS), nullptr, m_reprojectPS.ReleaseAndGetAddressOf()));
                winrt::check_hresult(m_submissionDevice->CreateVertexShader(
                    k_LayerVS, sizeof(k_LayerVS), nullptr, m_layerVS.ReleaseAndGetAddressOf()));
                winrt::check_hresult(m_submissionDevice->CreatePixelShader(
                    k_LayerPS, sizeof(k_LayerPS), nullptr, m_layerPS.ReleaseAndGetAddressOf()));
                winrt::check_hresult(m_submissionDevice->CreatePixelShader(
                    k_LayerCubePS, sizeof(k_LayerCubePS), nullptr, m_layerCubePS.ReleaseAndGetAddressOf()));
                {
                    D3D11_BUFFER_DESC bd{};
                    bd.ByteWidth = ((sizeof(LayerConstants) + 15) / 16) * 16;
                    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
                    bd.Usage = D3D11_USAGE_DYNAMIC;
                    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
                    winrt::check_hresult(
                        m_submissionDevice->CreateBuffer(&bd, nullptr, m_layerConstantsBuffer.ReleaseAndGetAddressOf()));
                    // quads are visible from both sides: no culling
                    D3D11_RASTERIZER_DESC rd{};
                    rd.FillMode = D3D11_FILL_SOLID;
                    rd.CullMode = D3D11_CULL_NONE;
                    rd.DepthClipEnable = TRUE;
                    winrt::check_hresult(
                        m_submissionDevice->CreateRasterizerState(&rd, m_noCullState.ReleaseAndGetAddressOf()));
                }
                {
                    D3D11_BUFFER_DESC desc{};
                    desc.ByteWidth = ((sizeof(ConstantsBuffer) + 15) / 16) * 16;
                    desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
                    desc.Usage = D3D11_USAGE_DYNAMIC;
                    desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;

                    winrt::check_hresult(
                        m_submissionDevice->CreateBuffer(&desc, nullptr, m_constantsBuffer.ReleaseAndGetAddressOf()));
                }
                {
                    D3D11_DEPTH_STENCIL_DESC desc{};
                    winrt::check_hresult(m_submissionDevice->CreateDepthStencilState(
                        &desc, m_noDepthTestState.ReleaseAndGetAddressOf()));
                }
                {
                    D3D11_BLEND_DESC desc{};
                    desc.RenderTarget[0].BlendEnable = TRUE;
                    desc.RenderTarget[0].SrcBlend = D3D11_BLEND_ONE;
                    desc.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
                    desc.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
                    desc.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
                    desc.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
                    desc.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
                    desc.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
                    winrt::check_hresult(
                        m_submissionDevice->CreateBlendState(&desc, m_blendState.ReleaseAndGetAddressOf()));
                }
                {
                    D3D11_SAMPLER_DESC desc{};
                    desc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
                    desc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
                    desc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
                    desc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
                    desc.MaxAnisotropy = 1;
                    desc.MinLOD = D3D11_MIP_LOD_BIAS_MIN;
                    desc.MaxLOD = D3D11_MIP_LOD_BIAS_MAX;
                    winrt::check_hresult(
                        m_submissionDevice->CreateSamplerState(&desc, m_linearSampler.ReleaseAndGetAddressOf()));
                }
            }

            CreateSbsRing();
            StartPublisher();

            if (!m_serverThread.joinable()) {
                m_terminateServerThread = false;
                m_serverThread = std::thread([&]() {
                    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
                    timeBeginPeriod(1);
                    const DWORD periodMs = (DWORD)(1000.f / m_displayRate);
                    LARGE_INTEGER lastTick{};
                    QueryPerformanceCounter(&lastTick);
                    while (!m_terminateServerThread) {
                        // The host paces the display: wait for its vsync event. If the host is absent or stalls,
                        // fall back to a local timer so the app never hangs.
                        DWORD r = WAIT_TIMEOUT;
                        if (m_ipc.state && m_ipc.vsync) {
                            r = WaitForSingleObject(m_ipc.vsync, periodMs * 2 + 5);
                        } else {
                            Sleep(periodMs);
                        }
                        LARGE_INTEGER now;
                        QueryPerformanceCounter(&now);
                        double tick = QpcToOvrTime(now);
                        if (r == WAIT_OBJECT_0 && m_ipc.state && m_ipc.state->host.nextVsyncTimeS > 0) {
                            tick = m_ipc.state->host.nextVsyncTimeS; // time of the host's latest vsync tick
                        }
                        if (m_ipc.state) {
                            m_ipc.state->shim.shimHeartbeat++;
                        }
                        (r == WAIT_OBJECT_0 ? m_vsyncFromHost : m_vsyncTimeouts)++;
                        std::unique_lock lock(m_frameMutex);
                        // ALVR's pacing clock schedules each tick; use its interval (the grid adapts to the client), not the nominal rate
                        double interval = 1.0 / m_displayRate;
                        if (r == WAIT_OBJECT_0 && m_ipc.state) {
                            const double hostInterval = m_ipc.state->host.vsyncIntervalS;
                            // follow ALVR's grid, but never let one odd tick (e.g. a very short remainder) distort the prediction
                            if (hostInterval > interval * 0.75 && hostInterval < interval * 1.25) {
                                interval = hostInterval;
                            }
                        }
                        m_vsyncInterval = interval;
                        if (!m_rsDisplayTimeOwned) { // else WaitForVsync's running start sets it from the grid
                            std::lock_guard dl(m_displayMutex);
                            m_nextFramePredictedDisplayTime = tick + interval + m_photonsTime;
                        }
                        m_latestTickS = tick;
                        // the host's tick event is a plain event: two ticks can coalesce into one wake. Count by tick time so the
                        // boundary grid (indexed by this counter) stays aligned with the host's ticks.
                        long long advance = 1;
                        if (r == WAIT_OBJECT_0 && m_prevHostTickS > 0) {
                            advance = std::max<long long>(1, (long long)std::llround((tick - m_prevHostTickS) / interval));
                            if (advance > 1) {
                                m_vsyncCoalesced += advance - 1;
                            }
                        }
                        if (r == WAIT_OBJECT_0) {
                            m_prevHostTickS = tick;
                        } else {
                            m_prevHostTickS = 0;
                        }
                        m_lastSignaledVsync += advance;
                        m_frameVsync.notify_all();
                    }
                    timeEndPeriod(1);
                });
            }

            TraceLoggingWriteStop(local, "HostDriver_SetSubmissionDevice");
        }

        void* CreateSwapchain(const ovrTextureSwapChainDesc& ovrDesc) override {
            // Never let a C++ exception unwind through the OVR C API into the runtime (VDXR): report and return null.
            try {
                return CreateSwapchainImpl(ovrDesc);
            } catch (const winrt::hresult_error& e) {
                ShimLog("CreateSwapchain failed: type=%d format=%d %ux%u array=%d mips=%d samples=%d misc=0x%x bind=0x%x -> HRESULT 0x%08x",
                        (int)ovrDesc.Type, (int)ovrDesc.Format, ovrDesc.Width, ovrDesc.Height, ovrDesc.ArraySize, ovrDesc.MipLevels,
                        ovrDesc.SampleCount, (unsigned)ovrDesc.MiscFlags, (unsigned)ovrDesc.BindFlags, (unsigned)e.code().value);
                shimlog::General("ERROR", "swapchain creation failed (format %d, %ux%u, array %d, samples %d): HRESULT 0x%08x",
                                 (int)ovrDesc.Format, ovrDesc.Width, ovrDesc.Height, ovrDesc.ArraySize, ovrDesc.SampleCount, (unsigned)e.code().value);
            } catch (const std::exception& e) {
                ShimLog("CreateSwapchain failed: %s", e.what());
                shimlog::General("ERROR", "swapchain creation failed: %s", e.what());
            }
            return nullptr;
        }

        void* CreateSwapchainImpl(const ovrTextureSwapChainDesc& ovrDesc) {
            TraceLocalActivity(local);
            TraceLoggingWriteStart(local,
                                   "HostDriver_CreateSwapchain",
                                   TLArg(ToString(ovrDesc.Type), "Type"),
                                   TLArg(ToString(ovrDesc.Format), "Format"),
                                   TLArg(ovrDesc.ArraySize, "ArraySize"),
                                   TLArg(ovrDesc.Width, "Width"),
                                   TLArg(ovrDesc.Height, "Height"),
                                   TLArg(ovrDesc.MipLevels, "MipLevels"),
                                   TLArg(ovrDesc.SampleCount, "SampleCount"),
                                   TLArg(!!ovrDesc.StaticImage, "StaticImage"),
                                   TLArg(ovrDesc.MiscFlags, "MiscFlags"),
                                   TLArg(ovrDesc.BindFlags, "BindFlags"));

            Swapchain* swapchain = nullptr;

            const DXGI_FORMAT format = ToDxgiTextureFormat(ovrDesc.Format);
            const bool isDepthSwapchain = (ovrDesc.BindFlags & ovrTextureBind_DX_DepthStencil) || IsDepthFormat(format);

            D3D11_TEXTURE2D_DESC desc{};
            desc.Format = format;
            // Per OVR documentation, depth swapchains are always typeless.
            if ((ovrDesc.MiscFlags & ovrTextureMisc_DX_Typeless) || isDepthSwapchain) {
                desc.Format = GetTypelessFormat(desc.Format);
            }
            desc.Width = ovrDesc.Width;
            desc.Height = ovrDesc.Height;
            desc.SampleDesc.Count = ovrDesc.SampleCount;
            desc.MipLevels = ovrDesc.MipLevels;
            desc.ArraySize = ovrDesc.ArraySize;
            if (ovrDesc.Type == ovrTexture_Cube && (desc.ArraySize == 0 || desc.ArraySize % 6 != 0)) {
                desc.ArraySize = 6 * std::max<UINT>(1, desc.ArraySize); // one cube = 6 faces (VDXR asks for ArraySize 1)
            }
            desc.BindFlags |= D3D11_BIND_SHADER_RESOURCE;
            if (ovrDesc.BindFlags & ovrTextureBind_DX_RenderTarget) {
                desc.BindFlags |= D3D11_BIND_RENDER_TARGET;
            }
            if (ovrDesc.BindFlags & ovrTextureBind_DX_UnorderedAccess) {
                desc.BindFlags |= D3D11_BIND_UNORDERED_ACCESS;
            }
            if (ovrDesc.BindFlags & ovrTextureBind_DX_DepthStencil) {
                desc.BindFlags |= D3D11_BIND_DEPTH_STENCIL;
            }
            desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
            if (ovrDesc.Type == ovrTexture_Cube) {
                desc.MiscFlags |= D3D11_RESOURCE_MISC_TEXTURECUBE;
            }
            if (ovrDesc.MiscFlags & ovrTextureMisc_AllowGenerateMips) {
                desc.MiscFlags |= D3D11_RESOURCE_MISC_GENERATE_MIPS;
            }

            swapchain = new Swapchain;
            swapchain->desc = ovrDesc;

            for (size_t i = 0; i < (ovrDesc.StaticImage ? 1 : k_SwapchainLength); i++) {
                // Create the typeless, app swapchain images.
                winrt::check_hresult(m_submissionDevice->CreateTexture2D(
                    &desc, nullptr, swapchain->textures[i].ReleaseAndGetAddressOf()));

                if (!isDepthSwapchain) {
                    // RTV for mirror rendering (single-slice swapchains only: cube/array textures need array views)
                    if ((ovrDesc.BindFlags & ovrTextureBind_DX_RenderTarget) && ovrDesc.ArraySize == 1 && ovrDesc.Type != ovrTexture_Cube) {
                        D3D11_RENDER_TARGET_VIEW_DESC desc{};
                        desc.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
                        desc.Format = format;
                        winrt::check_hresult(m_submissionDevice->CreateRenderTargetView(
                            swapchain->textures[i].Get(), &desc, swapchain->RTVs[i].ReleaseAndGetAddressOf()));
                    }
                    // SRV for compositing.
                    {
                        D3D11_SHADER_RESOURCE_VIEW_DESC desc{};
                        desc.Format = format;
                        if (ovrDesc.Type == ovrTexture_Cube) {
                            desc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURECUBE;
                            desc.TextureCube.MipLevels = -1;
                        } else {
                            desc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
                            desc.Texture2D.MipLevels = -1;
                        }
                        winrt::check_hresult(m_submissionDevice->CreateShaderResourceView(
                            swapchain->textures[i].Get(), &desc, swapchain->SRVs[i].ReleaseAndGetAddressOf()));
                    }
                }
            }
            {
                std::unique_lock lock(m_swapchainMutex);
                m_swapchains.insert(swapchain);
            }

            TraceLoggingWriteStop(local, "HostDriver_CreateSwapchain", TLPArg(swapchain, "Swapchain"));

            return swapchain;
        }

        void DestroySwapchain(void* swapchain) override {
            TraceLocalActivity(local);
            TraceLoggingWriteStart(local, "HostDriver_DestroySwapchain", TLPArg(swapchain, "Swapchain"));

            std::unique_lock lock(m_swapchainMutex);
            if (m_swapchains.erase(swapchain)) {
                Swapchain* swapchainObject = (Swapchain*)swapchain;
                delete swapchainObject;
            }

            TraceLoggingWriteStop(local, "HostDriver_DestroySwapchain");
        }

        ovrTextureSwapChainDesc GetSwapchainDesc(void* swapchain) const override {
            TraceLocalActivity(local);
            TraceLoggingWriteStart(local, "HostDriver_GetSwapchainDesc", TLPArg(swapchain, "Swapchain"));

            std::shared_lock lock(m_swapchainMutex);
            ovrTextureSwapChainDesc desc{};
            if (m_swapchains.count(swapchain)) {
                Swapchain* swapchainObject = (Swapchain*)swapchain;
                desc = swapchainObject->desc;
            }

            TraceLoggingWriteStop(local, "HostDriver_GetSwapchainDesc");

            return desc;
        }

        ID3D11Texture2D* GetSwapchainImage(void* swapchain, int index) const override {
            TraceLocalActivity(local);
            TraceLoggingWriteStart(
                local, "HostDriver_GetSwapchainImage", TLPArg(swapchain, "Swapchain"), TLArg(index, "Index"));

            ID3D11Texture2D* image = nullptr;

            std::shared_lock lock(m_swapchainMutex);
            if (m_swapchains.count(swapchain)) {
                Swapchain* swapchainObject = (Swapchain*)swapchain;
                if (index < (!swapchainObject->desc.StaticImage ? k_SwapchainLength : 1)) {
                    image = swapchainObject->textures[index].Get();
                }
            }

            TraceLoggingWriteStop(local, "HostDriver_GetSwapchainImage", TLPArg(image, "Image"));

            return image;
        }

        int GetSwapchainImageIndex(void* swapchain) const override {
            TraceLocalActivity(local);
            TraceLoggingWriteStart(local, "HostDriver_GetSwapchainImageIndex", TLPArg(swapchain, "Swapchain"));

            int index = -1;
            std::shared_lock lock(m_swapchainMutex);
            if (m_swapchains.count(swapchain)) {
                Swapchain* swapchainObject = (Swapchain*)swapchain;
                index = swapchainObject->lastCommittedIndex == 0
                            ? (!swapchainObject->desc.StaticImage ? (k_SwapchainLength - 1) : 0)
                            : (swapchainObject->lastCommittedIndex - 1);
            }

            TraceLoggingWriteStop(local, "HostDriver_GetSwapchainImageIndex", TLArg(index, "Index"));

            return index;
        }

        bool CommitSwapchainImage(void* swapchain) const override {
            TraceLocalActivity(local);
            TraceLoggingWriteStart(local, "HostDriver_CommitSwapchainImage", TLPArg(swapchain, "Swapchain"));

            bool success = false;

            std::shared_lock lock(m_swapchainMutex);
            if (m_swapchains.count(swapchain)) {
                Swapchain* swapchainObject = (Swapchain*)swapchain;
                swapchainObject->lastCommittedIndex++;
                if (swapchainObject->lastCommittedIndex >=
                    (!swapchainObject->desc.StaticImage ? k_SwapchainLength : 1)) {
                    swapchainObject->lastCommittedIndex = 0;
                }
                TraceLoggingWriteTagged(
                    local, "HostDriver_CommitSwapchainImage", TLArg(swapchainObject->lastCommittedIndex, "Index"));
                success = true;
            }

            TraceLoggingWriteStop(local, "HostDriver_CommitSwapchainImage", TLArg(success, "Success"));

            return success;
        }

        void SetMirrorTexture(void* swapchain) override {
            TraceLocalActivity(local);
            TraceLoggingWriteStart(local, "HostDriver_SetMirrorTexture", TLPArg(swapchain, "Swapchain"));

            if (swapchain) {
                std::shared_lock lock(m_swapchainMutex);
                if (m_swapchains.count(swapchain)) {
                    Swapchain* swapchainObject = (Swapchain*)swapchain;
                    m_mirrorRTV = swapchainObject->RTVs[0];
                    m_mirrorWidth = swapchainObject->desc.Width;
                    m_mirrorHeight = swapchainObject->desc.Height;
                }
            } else {
                m_mirrorRTV.Reset();
            }

            TraceLoggingWriteStop(local, "HostDriver_SetMirrorTexture");
        }

        double GetPredictedDisplayTime(long long frameIndex) const override {
            TraceLocalActivity(local);
            TraceLoggingWriteStart(local, "HostDriver_GetPredictedDisplayTime", TLArg(frameIndex, "Frame"));

            double predictedDisplayTime;
            std::lock_guard dl(m_displayMutex);
            // Base our prediction on the last successfully waited frame.
            if (frameIndex <= m_lastWaitedFrame) {
                predictedDisplayTime = m_nextFramePredictedDisplayTime;
            } else {
                const auto numFramesInTheFuture = frameIndex - m_lastWaitedFrame;
                predictedDisplayTime = m_nextFramePredictedDisplayTime + numFramesInTheFuture * m_vsyncInterval;
            }

            TraceLoggingWriteStop(
                local, "HostDriver_GetPredictedDisplayTime", TLArg(predictedDisplayTime, "PredictedDisplayTime"));

            return predictedDisplayTime;
        }

        void WaitForVsync(long long frameIndex) override {
            if (m_ipc.state && m_ipc.state->host.configSeq != m_hostConfigSeq) {
                LoadHostConfig(false);
                ShimLog("host config changed: FoV/eye offsets reloaded");
            }
            TraceLocalActivity(local);
            TraceLoggingWriteStart(local, "HostDriver_WaitForVsync", TLArg(frameIndex, "Frame"));

            std::unique_lock lock(m_frameMutex);
            // Wait when a future frame is being waited on, or when a frame was submitted since the last wait: VDXR's async
            // submission thread asks for frame id `m_frameCompleted`, which can still be the previous id when the app
            // finished its frame quickly, and skipping that wait let apps run past the display rate (97-111 fps at 90 Hz in
            // the first headset test), rendering frames that were never shown and loading the GPU the encoder shares.
            const bool submittedSinceWait = m_submittedSinceWait.exchange(false);
            m_waitCalls++;
            if (frameIndex <= m_lastWaitedFrame) {
                m_waitRepeatedIndex++;
            }
            if (frameIndex > m_lastWaitedFrame || submittedSinceWait) {
                using namespace std::chrono_literals;

                TraceLocalActivity(wait);
                TraceLoggingWriteStart(wait,
                                       "HostDriver_WaitForVsync_DirectModeComponent",
                                       TLArg(m_lastSignaledVsync, "LastSignaledVsync"));

                const float marginMs = m_ipc.state ? m_ipc.state->host.runningStartMs : 0.f;
                if (marginMs > 0.f && m_latestTickS > 0 && m_vsyncInterval > 0) {
                    // Running start (SteamVR's WaitGetPoses, Meta's Phase Sync): release the app so that its frame (release ->
                    // composed and GPU-complete, the envelope m_appTimeS) lands this margin before the host's next compositor
                    // boundary, one release per boundary. An app slower than a display period is released at once and free-runs:
                    // the host picks whatever frame is complete at each boundary and the headset repeats on the empty ones.
                    const double period = m_vsyncInterval;
                    const double offset = m_ipc.state->host.boundaryOffsetMs * 0.001;
                    const double lead = m_appTimeS.load() + marginMs * 0.001;
                    if (m_lastTargetBoundary == 0) {
                        m_lastTargetBoundary = m_lastSignaledVsync;
                    }
                    // boundary j (counted in host ticks) is at m_latestTickS + (j - m_lastSignaledVsync) * period + offset
                    auto boundaryTime = [&](long long j) {
                        return m_latestTickS + (double)(j - m_lastSignaledVsync) * period + offset;
                    };
                    long long target = m_lastTargetBoundary + 1;
                    double now = QpcSeconds();
                    while (boundaryTime(target) - lead < now - period) {
                        target++; // far behind (paused app, very long frame): skip boundaries more than a period in the past
                    }
                    // Sample-arrival alignment: the client's tracking samples arrive once per period at some phase a after the
                    // boundary before this release's boundary. A release just before that arrival reads a sample one period old
                    // (head pose ~10 ms stale), and one drifting across it makes two frames read the same sample then skip one.
                    // So the release is kept off a by a guard g: slightly later than the sample's arrival when that costs at most
                    // m_phaseDelayMax of the running-start margin (fresh pose, the usual case), else slightly before it (stable,
                    // one period older pose). Moving from one side to the other happens only when the wanted phase has moved by
                    // more than that, so the sample choice flips rarely instead of on every wobble.
                    static const bool noAlign = !EnvSet("OVRSHIM_ALIGN_RELEASE"); // opt-in: see SelectHostHead
                    auto alignedRelease = [&](double releaseAt) {
                        if (noAlign) {
                            return releaseAt;
                        }
                        const double arrival = m_ipc.state->host.headHostTimeS;
                        const double b0 = boundaryTime(target) - period;
                        if (arrival <= 0) {
                            return releaseAt;
                        }
                        double a = std::fmod(arrival - b0, period);
                        if (a < 0) {
                            a += period;
                        }
                        const double g = 0.0005;
                        const double rp = releaseAt - b0;
                        if (rp < 0 || rp > period || a + g > period - 0.5 * marginMs * 0.001) {
                            return releaseAt;
                        }
                        const double d = (a + g) - rp; // how much later the fresh side is than the wanted release
                        if (d <= 0) {
                            m_phaseFresh = true;
                            return releaseAt;
                        }
                        // hysteresis: a fresh-side release is delayed up to 2 ms (of the 2 ms margin plus the lead's own wobble)
                        // before giving the fresh pose up; a stale-side release comes back only when it costs under 0.5 ms
                        if (m_phaseFresh ? d <= 0.002 : d <= 0.0005) {
                            m_phaseFresh = true;
                            return b0 + a + g;
                        }
                        m_phaseFresh = false;
                        return std::min(releaseAt, b0 + a - g);
                    };
                    const double plannedRelease = alignedRelease(boundaryTime(target) - lead);
                    for (;;) {
                        now = QpcSeconds();
                        const double releaseAt = alignedRelease(boundaryTime(target) - lead); // ticks arriving meanwhile refine the grid
                        if (releaseAt <= now) {
                            break;
                        }
                        const auto waitFor = std::chrono::duration_cast<std::chrono::steady_clock::duration>(
                            std::chrono::duration<double>(std::min(releaseAt - now, 0.1)));
                        m_frameVsync.wait_until(lock, std::chrono::steady_clock::now() + waitFor);
                    }
                    m_lastTargetBoundary = target;
                    // The display time follows the grid boundary the app was released for, not whichever host tick happens to be the
                    // latest when it wakes: a release landing within a wake-up jitter of a tick otherwise got the previous
                    // boundary's time (duplicate timestamps, then a skip).
                    {
                        // base time and the frame it belongs to change together: VDXR asks GetPredictedDisplayTime from another thread
                        // and extrapolates by (frameIndex - m_lastWaitedFrame) periods
                        std::lock_guard dl(m_displayMutex);
                        m_nextFramePredictedDisplayTime = boundaryTime(target) - offset + m_photonsTime;
                        m_lastWaitedFrame = std::max(m_lastWaitedFrame, frameIndex);
                        m_rsDisplayTimeOwned = true;
                    }
                    if (plannedRelease < now - 0.0005) {
                        m_releasesLate++;
                    }
                } else {
                    // no running start: release at the host's next display tick
                    m_rsDisplayTimeOwned = false;
                    const auto lastSignaledVsync = m_lastSignaledVsync;
                    if (!m_frameVsync.wait_for(lock, 100ms, [&]() { return m_lastSignaledVsync > lastSignaledVsync; })) {
                        m_waitTimeouts++;
                    }
                }
                m_releases++;
                m_waitWaited++;
                lock.unlock();
                {
                    // GPU throttle, like a real runtime (and SteamVR's WaitGetPoses): the app may start a new frame only when at
                    // most one of its submitted frames is still completing on the GPU. Without it a GPU-bound app runs several
                    // frames ahead, its frames then complete in bunches (two in one display period, then none) and the stream
                    // skips and repeats instead of showing one frame per period.
                    std::unique_lock plock(m_publishMutex);
                    if (!m_publishCv.wait_for(plock, 50ms, [&] { return m_publishQueue.size() <= 1; })) {
                        m_gpuThrottleTimeouts++;
                    }
                }
                lock.lock();
                m_lastReleaseS = QpcSeconds();
                m_lastWaitedFrame = std::max(m_lastWaitedFrame, frameIndex);

                TraceLoggingWriteStop(wait,
                                      "HostDriver_WaitForVsync_DirectModeComponent",
                                      TLArg(m_lastSignaledVsync, "LastSignaledVsync"));
            }

            TraceLoggingWriteStop(local,
                                  "HostDriver_WaitForVsync",
                                  TLArg(m_nextFramePredictedDisplayTime, "NextFramePredictedDisplayTime"));
        }

        static bool IsSrgb(DXGI_FORMAT f) {
            return f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || f == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB ||
                   f == DXGI_FORMAT_B8G8R8X8_UNORM_SRGB;
        }

        enum class TargetMode {
            DirectEncode, // UNORM target: sRGB-sourced content is encoded to sRGB values as it is drawn (single-layer fast path)
            Linear,       // FP16 linear target: all sources converted to linear, encoded by a final pass
            Plain         // mirror texture: values pass through unchanged
        };

        // Exponent for the final encoded value: the client decodes with pow(v, encoding gamma); the user's display gamma (> 1 =
        // brighter) is folded into the same pass.
        void UpdateOutputGamma() {
            float user = m_ipc.state ? m_ipc.state->host.userGamma : 1.f;
            if (!(user > 0.2f && user < 5.f)) {
                user = 1.f;
            }
            m_outputGamma = 1.f / (m_encodingGamma * user);
        }

        // The user's colour correction (VisionALVR.exe sliders, read from the IPC block every frame), for the draw that writes the
        // encoded value only.
        void FillColor(bool final, float& brightness, float& contrast, float& saturation, float& sharpening, int& finalPass) const {
            brightness = contrast = saturation = sharpening = 0.f;
            finalPass = final ? 1 : 0;
            if (!final || !m_ipc.state) {
                return;
            }
            const auto& h = m_ipc.state->host;
            auto clampf = [](float v, float lo, float hi) { return v != v ? 0.f : (v < lo ? lo : (v > hi ? hi : v)); };
            brightness = clampf(h.colorBrightness, -0.5f, 0.5f);
            contrast = clampf(h.colorContrast, -0.5f, 0.5f);
            saturation = clampf(h.colorSaturation, -1.f, 1.f);
            sharpening = clampf(h.colorSharpening, 0.f, 2.f);
        }

        // Follows the host's debug switch: the verbose log goes to its debug session folder.
        void UpdateDebugDir() {
            if (!m_ipc.state) {
                return;
            }
            const auto& h = m_ipc.state->host;
            wchar_t dir[260];
            memcpy(dir, h.debugDir, sizeof(dir));
            dir[259] = 0;
            shimlog::SetDebugDir(h.debugOn ? std::wstring(dir) : std::wstring());
        }

        // The negotiated ALVR encoding gamma is applied where the final encoded value is written (the shared slot), nowhere else.
        float OutputGamma(TargetMode mode) const {
            return mode == TargetMode::DirectEncode ? m_outputGamma : 1.f;
        }

        static void SrgbFlags(TargetMode mode, bool sourceIsSrgb, int& encode, int& decode) {
            encode = (mode == TargetMode::DirectEncode && sourceIsSrgb) ? 1 : 0;
            decode = (mode == TargetMode::Linear && !sourceIsSrgb) ? 1 : 0;
        }

        static bool IsComposedLayerType(const ovrLayerHeader* h) {
            if (!h) {
                return false;
            }
            switch (h->Type) {
            case ovrLayerType_EyeFov:
            case ovrLayerType_EyeFovDepth:
            case ovrLayerType_Quad:
            case ovrLayerType_Cylinder:
            case ovrLayerType_Cube:
                return true;
            default:
                return false;
            }
        }

        void UploadLayerConstants(const LayerConstants& c) {
            D3D11_MAPPED_SUBRESOURCE mapped;
            winrt::check_hresult(
                m_submissionContext->Map(m_layerConstantsBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped));
            memcpy(mapped.pData, &c, sizeof(c));
            m_submissionContext->Unmap(m_layerConstantsBuffer.Get(), 0);
        }

        static D3D11_VIEWPORT EyeViewport(uint32_t eye, uint32_t width, uint32_t height) {
            D3D11_VIEWPORT viewport{};
            viewport.Width = width / 2.f;
            viewport.TopLeftX = eye * viewport.Width;
            viewport.Height = (float)height;
            viewport.MaxDepth = 1.f;
            return viewport;
        }

        // Draws all supported layers into `rtv` (left eye in the left half, right eye in the right half), in submission order.
        bool ComposeLayers(const std::vector<const ovrLayerHeader*>& layers,
                           ID3D11RenderTargetView* rtv,
                           uint32_t width,
                           uint32_t height,
                           TargetMode mode) {
            const float clearColor[] = {0.f, 0.f, 0.f, 1.f};
            m_submissionContext->ClearRenderTargetView(rtv, clearColor);

            m_submissionContext->OMSetRenderTargets(1, &rtv, nullptr);
            m_submissionContext->RSSetState(m_noCullState.Get());
            m_submissionContext->PSSetSamplers(0, 1, m_linearSampler.GetAddressOf());
            m_submissionContext->OMSetDepthStencilState(m_noDepthTestState.Get(), 0xff);
            m_submissionContext->OMSetBlendState(m_blendState.Get(), nullptr, 0xffffffff);

            // Eye poses (in tracking space) that quad/cylinder/cube layers are drawn from: the pose the projection layer
            // was rendered with (so world-locked layers line up with the world), else the head pose the app last read.
            ovrPosef eyeView[ovrEye_Count];
            bool haveProjection = false;
            for (const ovrLayerHeader* h : layers) {
                if (h && (h->Type == ovrLayerType_EyeFov || h->Type == ovrLayerType_EyeFovDepth)) {
                    const auto* l = (const ovrLayerEyeFov*)h;
                    eyeView[0] = l->RenderPose[0];
                    eyeView[1] = l->RenderPose[1];
                    haveProjection = true;
                    break;
                }
            }
            if (!haveProjection) {
                ovrPosef head;
                {
                    std::lock_guard lock(m_lastHeadMutex);
                    head = m_lastHeadPose;
                }
                for (int e = 0; e < ovrEye_Count; e++) {
                    eyeView[e] = OVR::Posef(head) * OVR::Posef(m_eyePose[e]);
                }
            }

            bool ok = true;
            {
                std::shared_lock lock(m_swapchainMutex);

                for (const ovrLayerHeader* layerHeader : layers) {
                    const ovrLayer_Union* layer = (ovrLayer_Union*)layerHeader;

                    // Per OVR documentation, this is legal and equivalent to ovrLayerType_Disabled.
                    if (!layer) {
                        continue;
                    }
                    const unsigned flags = layer->Header.Flags;
                    const bool skipLeft = (flags & kLayerFlagDisableLeft) != 0;
                    const bool skipRight = (flags & kLayerFlagDisableRight) != 0;

                    if (layer->Header.Type == ovrLayerType_EyeFov || layer->Header.Type == ovrLayerType_EyeFovDepth) {
                        const auto eyeFov = &layer->EyeFov;

                        m_submissionContext->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                        m_submissionContext->VSSetShader(m_reprojectVS.Get(), nullptr, 0);
                        m_submissionContext->PSSetShader(m_reprojectPS.Get(), nullptr, 0);
                        m_submissionContext->VSSetConstantBuffers(0, 1, m_constantsBuffer.GetAddressOf());
                        m_submissionContext->PSSetConstantBuffers(0, 1, m_constantsBuffer.GetAddressOf());

                        ConstantsBuffer constants{};
                        constants.flipY = flags & ovrLayerFlag_TextureOriginAtBottomLeft;
                        for (uint32_t eye = 0; eye < ovrEye_Count; eye++) {
                            if ((eye == 0 && skipLeft) || (eye == 1 && skipRight)) {
                                continue;
                            }
                            const auto baseProjection = LoadOvrProjection(m_eyeFov[eye], 0.01f, 1000.f);
                            const auto projection = LoadOvrProjection(eyeFov->Fov[eye], 0.01f, 1000.f);

                            // TODO: Reproject for mismatched eye poses (not just mutable FOV).
                            DirectX::XMStoreFloat4x4(
                                &constants.reprojectionMatrix,
                                DirectX::XMMatrixTranspose(DirectX::XMMatrixInverse(nullptr, baseProjection) *
                                                           projection));

                            // OVR allows specifying null texture for the right eye.
                            const auto swapchain = eye == 0 || !eyeFov->ColorTexture[eye] ? eyeFov->ColorTexture[0]
                                                                                          : eyeFov->ColorTexture[eye];
                            if (!m_swapchains.count(swapchain)) {
                                ok = false;
                                continue;
                            }
                            Swapchain* swapchainObject = (Swapchain*)swapchain;
                            SrgbFlags(mode, IsSrgb(ToDxgiTextureFormat(swapchainObject->desc.Format)),
                                      constants.encodeSrgb, constants.decodeSrgb);
                            constants.outputGamma = OutputGamma(mode);
                            FillColor(mode == TargetMode::DirectEncode, constants.brightness, constants.contrast, constants.saturation,
                                      constants.sharpening, constants.finalPass);

                            constants.imageRectNormalized.offset = {
                                (float)eyeFov->Viewport[eye].Pos.x / swapchainObject->desc.Width,
                                (float)eyeFov->Viewport[eye].Pos.y / swapchainObject->desc.Height};
                            constants.imageRectNormalized.extent = {
                                (float)eyeFov->Viewport[eye].Size.w / swapchainObject->desc.Width,
                                (float)eyeFov->Viewport[eye].Size.h / swapchainObject->desc.Height};

                            D3D11_MAPPED_SUBRESOURCE mappedResources;
                            winrt::check_hresult(m_submissionContext->Map(
                                m_constantsBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mappedResources));
                            memcpy(mappedResources.pData, &constants, sizeof(constants));
                            m_submissionContext->Unmap(m_constantsBuffer.Get(), 0);

                            const D3D11_VIEWPORT viewport = EyeViewport(eye, width, height);
                            m_submissionContext->RSSetViewports(1, &viewport);
                            m_submissionContext->PSSetShaderResources(
                                0, 1, swapchainObject->SRVs[swapchainObject->ComposeIndex()].GetAddressOf());
                            m_submissionContext->Draw(3, 0);
                        }
                    } else if (layer->Header.Type == ovrLayerType_Quad || layer->Header.Type == ovrLayerType_Cylinder) {
                        // Quad and cylinder share ColorTexture / Viewport / pose at the same offsets (checked by VDXR too).
                        const bool isCylinder = layer->Header.Type == ovrLayerType_Cylinder;
                        const auto& q = layer->Quad;
                        if (!m_swapchains.count(q.ColorTexture)) {
                            ok = false;
                            continue;
                        }
                        Swapchain* sc = (Swapchain*)q.ColorTexture;
                        const bool headLocked = (flags & ovrLayerFlag_HeadLocked) != 0;
                        const ovrPosef layerPose = isCylinder ? layer->Cylinder.CylinderPoseCenter : q.QuadPoseCenter;

                        LayerConstants c{};
                        c.kind = isCylinder ? LAYER_KIND_CYLINDER : LAYER_KIND_QUAD;
                        c.flipY = (flags & ovrLayerFlag_TextureOriginAtBottomLeft) ? 1 : 0;
                        c.premultiply = 0; // VDXR already hands OVR premultiplied textures (alpha pre-processing in its swapchain shader)
                        c.segments = 64;
                        SrgbFlags(mode, IsSrgb(ToDxgiTextureFormat(sc->desc.Format)), c.encodeSrgb, c.decodeSrgb);
                        c.outputGamma = OutputGamma(mode);
                        FillColor(mode == TargetMode::DirectEncode, c.brightness, c.contrast, c.saturation, c.sharpening, c.finalPass);
                        c.uvOffset = {(float)q.Viewport.Pos.x / sc->desc.Width, (float)q.Viewport.Pos.y / sc->desc.Height};
                        c.uvExtent = {(float)q.Viewport.Size.w / sc->desc.Width, (float)q.Viewport.Size.h / sc->desc.Height};
                        if (isCylinder) {
                            c.radius = layer->Cylinder.CylinderRadius;
                            c.angle = layer->Cylinder.CylinderAngle;
                            c.aspect = layer->Cylinder.CylinderAspectRatio > 0.f ? layer->Cylinder.CylinderAspectRatio : 1.f;
                        } else {
                            c.size = {q.QuadSize.x, q.QuadSize.y};
                        }

                        m_submissionContext->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
                        m_submissionContext->VSSetShader(m_layerVS.Get(), nullptr, 0);
                        m_submissionContext->PSSetShader(m_layerPS.Get(), nullptr, 0);
                        m_submissionContext->VSSetConstantBuffers(0, 1, m_layerConstantsBuffer.GetAddressOf());
                        m_submissionContext->PSSetConstantBuffers(0, 1, m_layerConstantsBuffer.GetAddressOf());
                        m_submissionContext->PSSetShaderResources(0, 1, sc->SRVs[sc->ComposeIndex()].GetAddressOf());

                        for (uint32_t eye = 0; eye < ovrEye_Count; eye++) {
                            if ((eye == 0 && skipLeft) || (eye == 1 && skipRight)) {
                                continue;
                            }
                            // head-locked layers are in head space: the eye sits at its offset from the head
                            const auto view = LoadInvertedOvrPose(headLocked ? m_eyePose[eye] : eyeView[eye]);
                            const auto proj = DirectX::XMMatrixTranspose(LoadOvrProjection(m_eyeFov[eye], 0.05f, 100.f));
                            DirectX::XMStoreFloat4x4(&c.worldViewProj,
                                                     DirectX::XMMatrixTranspose(LoadOvrPose(layerPose) * view * proj));
                            UploadLayerConstants(c);

                            const D3D11_VIEWPORT viewport = EyeViewport(eye, width, height);
                            m_submissionContext->RSSetViewports(1, &viewport);
                            m_submissionContext->Draw(isCylinder ? 2 * (c.segments + 1) : 4, 0);
                        }
                    } else if (layer->Header.Type == ovrLayerType_Cube) {
                        const auto& cube = layer->Cube;
                        if (!m_swapchains.count(cube.CubeMapTexture)) {
                            ok = false;
                            continue;
                        }
                        Swapchain* sc = (Swapchain*)cube.CubeMapTexture;
                        const bool headLocked = (flags & ovrLayerFlag_HeadLocked) != 0;

                        LayerConstants c{};
                        c.kind = LAYER_KIND_CUBE;
                        SrgbFlags(mode, IsSrgb(ToDxgiTextureFormat(sc->desc.Format)), c.encodeSrgb, c.decodeSrgb);
                        c.outputGamma = OutputGamma(mode);
                        FillColor(mode == TargetMode::DirectEncode, c.brightness, c.contrast, c.saturation, c.sharpening, c.finalPass);

                        m_submissionContext->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                        m_submissionContext->VSSetShader(m_layerVS.Get(), nullptr, 0);
                        m_submissionContext->PSSetShader(m_layerCubePS.Get(), nullptr, 0);
                        m_submissionContext->VSSetConstantBuffers(0, 1, m_layerConstantsBuffer.GetAddressOf());
                        m_submissionContext->PSSetConstantBuffers(0, 1, m_layerConstantsBuffer.GetAddressOf());
                        m_submissionContext->PSSetShaderResources(0, 1, sc->SRVs[sc->ComposeIndex()].GetAddressOf());

                        for (uint32_t eye = 0; eye < ovrEye_Count; eye++) {
                            if ((eye == 0 && skipLeft) || (eye == 1 && skipRight)) {
                                continue;
                            }
                            using namespace DirectX;
                            const ovrPosef& vp = headLocked ? m_eyePose[eye] : eyeView[eye];
                            const XMMATRIX proj = XMMatrixTranspose(LoadOvrProjection(m_eyeFov[eye], 0.05f, 100.f));
                            // view direction -> world (eye orientation) -> cube space (inverse cube orientation), then the
                            // handedness flip: the OVR cube map is left-handed (looking down -Z the +X face is on the left).
                            const XMVECTOR qEye = XMLoadFloat4((const XMFLOAT4*)&vp.Orientation);
                            const XMVECTOR qCube = XMLoadFloat4((const XMFLOAT4*)&cube.Orientation);
                            const XMMATRIX toCube = XMMatrixRotationQuaternion(qEye) *
                                                    XMMatrixRotationQuaternion(XMQuaternionConjugate(qCube)) *
                                                    XMMatrixScaling(-1.f, 1.f, 1.f);
                            XMStoreFloat4x4(&c.invProj, XMMatrixTranspose(XMMatrixInverse(nullptr, proj)));
                            XMStoreFloat4x4(&c.viewToCube, XMMatrixTranspose(toCube));
                            UploadLayerConstants(c);

                            const D3D11_VIEWPORT viewport = EyeViewport(eye, width, height);
                            m_submissionContext->RSSetViewports(1, &viewport);
                            m_submissionContext->Draw(3, 0);
                        }
                    }
                    // Disabled / unsupported layer types are skipped.
                }
            }

            ID3D11RenderTargetView* nullRTV[] = {nullptr};
            m_submissionContext->OMSetRenderTargets(1, nullRTV, nullptr);
            ID3D11ShaderResourceView* nullSRV[] = {nullptr};
            m_submissionContext->PSSetShaderResources(0, 1, nullSRV);
            return ok;
        }

        // Linear FP16 frame -> sRGB-encoded UNORM slot (both eyes at once).
        void EncodeLinearToSlot(ID3D11RenderTargetView* rtv, uint32_t width, uint32_t height) {
            LayerConstants c{};
            c.kind = LAYER_KIND_FULLSCREEN;
            c.uvOffset = {0.f, 0.f};
            c.uvExtent = {1.f, 1.f};
            c.encodeSrgb = 1;
            c.outputGamma = m_outputGamma;
            FillColor(true, c.brightness, c.contrast, c.saturation, c.sharpening, c.finalPass);
            UploadLayerConstants(c);
            m_submissionContext->OMSetRenderTargets(1, &rtv, nullptr);
            m_submissionContext->RSSetState(m_noCullState.Get());
            m_submissionContext->OMSetBlendState(nullptr, nullptr, 0xffffffff); // overwrite
            D3D11_VIEWPORT viewport{};
            viewport.Width = (float)width;
            viewport.Height = (float)height;
            viewport.MaxDepth = 1.f;
            m_submissionContext->RSSetViewports(1, &viewport);
            m_submissionContext->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            m_submissionContext->VSSetShader(m_layerVS.Get(), nullptr, 0);
            m_submissionContext->PSSetShader(m_layerPS.Get(), nullptr, 0);
            m_submissionContext->VSSetConstantBuffers(0, 1, m_layerConstantsBuffer.GetAddressOf());
            m_submissionContext->PSSetConstantBuffers(0, 1, m_layerConstantsBuffer.GetAddressOf());
            m_submissionContext->PSSetSamplers(0, 1, m_linearSampler.GetAddressOf());
            m_submissionContext->PSSetShaderResources(0, 1, m_linearSRV.GetAddressOf());
            m_submissionContext->Draw(3, 0);
            ID3D11RenderTargetView* nullRTV[] = {nullptr};
            m_submissionContext->OMSetRenderTargets(1, nullRTV, nullptr);
            ID3D11ShaderResourceView* nullSRV[] = {nullptr};
            m_submissionContext->PSSetShaderResources(0, 1, nullSRV);
        }

        bool SubmitFrame(const std::vector<const ovrLayerHeader*>& layers) override {
            TraceLocalActivity(local);
            TraceLoggingWriteStart(local, "HostDriver_SubmitFrame");
            const double tSubmitStart = QpcSeconds();
            m_submittedSinceWait = true;
            if (m_ipc.state) {
                UpdateOutputGamma(); // the user's gamma can change live
                if (m_framesSubmitted % 90 == 0) {
                    UpdateDebugDir();
                }
            }
            LogLayersIfChanged(layers);

            bool ok = true;
            if (m_mirrorRTV) {
                ok &= ComposeLayers(layers, m_mirrorRTV.Get(), m_mirrorWidth, m_mirrorHeight, TargetMode::Plain);
            }

            if (m_ipc.state && m_sbsWidth) {
                const double composeStart = QpcSeconds();
                const uint32_t slot = PickSlot();
                int composed = 0;
                for (const ovrLayerHeader* h : layers) {
                    composed += IsComposedLayerType(h) ? 1 : 0;
                }
                if (composed > 1 && m_linearRTV) {
                    // Several layers (e.g. quads over the world): blend in linear light like a real compositor, then
                    // sRGB-encode once into the shared slot.
                    ok &= ComposeLayers(layers, m_linearRTV.Get(), m_sbsWidth, m_sbsHeight, TargetMode::Linear);
                    EncodeLinearToSlot(m_sbsRTV[slot].Get(), m_sbsWidth, m_sbsHeight);
                } else {
                    ok &= ComposeLayers(layers, m_sbsRTV[slot].Get(), m_sbsWidth, m_sbsHeight, TargetMode::DirectEncode);
                }

                const uint64_t clientTs = FrameClientTimestamp(layers);
                FrameTraceRow trace;
#ifdef VISIONALVR_TELEMETRY
                trace.tSubmit = tSubmitStart;
                for (const ovrLayerHeader* lh : layers) {
                    if (lh && (lh->Type == ovrLayerType_EyeFov || lh->Type == ovrLayerType_EyeFovDepth)) {
                        const ovrLayerEyeFov* e = (const ovrLayerEyeFov*)lh;
                        trace.displayTime = e->SensorSampleTime;
                        trace.q[0] = e->RenderPose[0].Orientation.x;
                        trace.q[1] = e->RenderPose[0].Orientation.y;
                        trace.q[2] = e->RenderPose[0].Orientation.z;
                        trace.q[3] = e->RenderPose[0].Orientation.w;
                        trace.lp[0] = e->RenderPose[0].Position.x;
                        trace.lp[1] = e->RenderPose[0].Position.y;
                        trace.lp[2] = e->RenderPose[0].Position.z;
                        break;
                    }
                }
                {
                    std::lock_guard rl(m_lastHeadMutex);
                    const PoseRead* best = nullptr;
                    for (const auto& r : m_readRing) {
                        if (r.ts == clientTs && (!best || std::abs(r.absTime - trace.displayTime) < std::abs(best->absTime - trace.displayTime))) {
                            best = &r;
                        }
                    }
                    if (best) {
                        trace.tRead = best->tRead;
                        trace.tArrive = best->tArrive;
                        trace.predicted = best->predicted;
                        trace.repeated = best->repeated;
                        memcpy(trace.sq, best->sq, sizeof(trace.sq));
                        memcpy(trace.sp, best->sp, sizeof(trace.sp));
                    }
                }
#endif
                ++m_framesSubmitted;
                if (m_framesSubmitted % 900 == 0) {
                    const auto& st = m_ipc.state->shim;
                    const double nowS = QpcSeconds();
                    const double rate = m_statT0 > 0 ? (m_lastSignaledVsync - m_statTicks0) / (nowS - m_statT0) : 0;
                    const double fps = m_statT0 > 0 ? 900.0 / (nowS - m_statT0) : 0;
                    m_statT0 = nowS;
                    m_statTicks0 = m_lastSignaledVsync;
                    ShimLog("frames %llu: %.1f fps, vsync %.1f Hz (host %u events, %u local timeouts), last compose+GPU %.2f ms, "
                            "layers %d, ts matched %u (by display time %llu) / fallback %u; vsync ticks %lld, "
                            "WaitToBeginFrame calls %llu (waited %llu, repeated index %llu, timeouts %llu)",
                            (unsigned long long)m_framesSubmitted, fps, rate, (unsigned)m_vsyncFromHost, (unsigned)m_vsyncTimeouts,
                            st.lastComposeMs, composed, st.tsMatched, (unsigned long long)m_tsByDisplayTime, st.tsFallback,
                            m_lastSignaledVsync, (unsigned long long)m_waitCalls, (unsigned long long)m_waitWaited,
                            (unsigned long long)m_waitRepeatedIndex, (unsigned long long)m_waitTimeouts);
                    ShimLog("pacing: running start %s (margin %.1f ms, boundary offset %.1f ms), app time envelope %.2f ms, releases %llu "
                            "(late %llu), GPU throttle timeouts %llu, slot waits %llu, host busy mask 0x%x",
                            m_ipc.state->host.runningStartMs > 0.f ? "on" : "off", m_ipc.state->host.runningStartMs,
                            m_ipc.state->host.boundaryOffsetMs, m_appTimeS.load() * 1000.0, (unsigned long long)m_releases.load(),
                            (unsigned long long)m_releasesLate.load(), (unsigned long long)m_gpuThrottleTimeouts,
                            (unsigned long long)m_slotBusyWaits.load(), (unsigned)m_ipc.state->host.slotBusyMask);
                    auto pct = [](std::vector<float>& v, double q) {
                        if (v.empty()) {
                            return 0.0f;
                        }
                        const size_t i = std::min(v.size() - 1, (size_t)(q * v.size()));
                        std::nth_element(v.begin(), v.begin() + i, v.end());
                        return v[i];
                    };
                    const size_t n = m_diagAngleMdeg.size();
                    ShimLog("pose match (%zu frames): residual mdeg p50 %.3f p95 %.3f max %.3f; picked newest/2nd/3rd/older %u/%u/%u/%u, "
                            "age behind newest ms p50 %.1f p95 %.1f max %.1f; ties 1/2/3+ %u/%u/%u; no pose for display time %u; "
                            "same timestamp as the previous frame %u; in-order consume: waited-repeat %llu catch-up skips %llu predicted %llu",
                            n, pct(m_diagAngleMdeg, 0.5), pct(m_diagAngleMdeg, 0.95), pct(m_diagAngleMdeg, 1.0), m_diagPickIdx[0],
                            m_diagPickIdx[1], m_diagPickIdx[2], m_diagPickIdx[3], pct(m_diagAgeMs, 0.5), pct(m_diagAgeMs, 0.95),
                            pct(m_diagAgeMs, 1.0), m_diagTies[0], m_diagTies[1], m_diagTies[2], m_diagNoDisplayTime, m_diagSameTs,
                            (unsigned long long)m_consumeRepeats, (unsigned long long)m_consumeSkips, (unsigned long long)m_consumePredicted);
                    m_diagAngleMdeg.clear();
                    m_diagAgeMs.clear();
                    memset(m_diagPickIdx, 0, sizeof(m_diagPickIdx));
                    memset(m_diagTies, 0, sizeof(m_diagTies));
                    m_diagNoDisplayTime = 0;
                    m_diagSameTs = 0;
                }
                if (m_publishThread.joinable()) {
                    // The host may only read the slot once the GPU is done with it, and that includes the app's own rendering of
                    // this frame (same device, in order). Waiting here would block the app's render thread for the whole GPU frame
                    // (CPU and GPU work serialised: a 6 ms CPU + 8 ms GPU frame drops from 90 to 45 fps). Like a real compositor,
                    // return now: DXGI signals an event when the GPU is done and the publisher thread hands the frame to the host.
                    std::unique_lock lock(m_publishMutex);
                    // at most 2 frames in flight (the ring has 4 slots; the host may be encoding a third)
                    m_publishCv.wait_for(lock, std::chrono::milliseconds(50), [&] { return m_publishQueue.size() < 2; });
                    ResetEvent(m_gpuDone[slot]);
                    if (FAILED(m_dxgiDevice2->EnqueueSetEvent(m_gpuDone[slot]))) {
                        // still hand it to the publisher (the only thread that publishes while it runs), already complete
                        WaitGpuIdleSpin();
                        SetEvent(m_gpuDone[slot]);
                    }
                    m_publishQueue.push_back({slot, clientTs, composeStart, m_lastReleaseS.load(), trace});
                    lock.unlock();
                    m_publishCv.notify_all();
                } else {
                    // OVRSHIM_SYNC_SUBMIT=1: wait for the GPU on the app thread (previous behaviour, kill switch)
                    WaitGpuIdleSpin();
                    PublishFrame(slot, clientTs, composeStart, m_lastReleaseS.load(), trace);
                }
            }

            if (!m_ipc.state) {
                ProcessActionKeys();

                // "Maintain" the pose time.
                LARGE_INTEGER now{};
                QueryPerformanceCounter(&now);

                std::unique_lock lock(m_hmdPoseMutex);
                m_hmdPose.TimeInSeconds = QpcToOvrTime(now);
                std::unique_lock lock2(m_controllerMutex);
                m_controllerPose[0].TimeInSeconds = m_controllerPose[1].TimeInSeconds =
                    m_controllerButtons.TimeInSeconds = m_hmdPose.TimeInSeconds;
            }

            TraceLoggingWriteStop(local, "HostDriver_SubmitFrame");

            return ok;
        }

        // The shim's own spin wait (OVRSHIM_SYNC_SUBMIT=1, or if EnqueueSetEvent fails): until the GPU is done, at most 50 ms.
        void WaitGpuIdleSpin() {
            m_submissionContext->End(m_doneQuery.Get());
            m_submissionContext->Flush();
            const double t0 = QpcSeconds();
            BOOL done = FALSE;
            while (m_submissionContext->GetData(m_doneQuery.Get(), &done, sizeof(done), 0) != S_OK || !done) {
                if (QpcSeconds() - t0 > 0.05) {
                    break;
                }
                _mm_pause();
            }
        }

        // Hands a composed (GPU-complete) slot to the host. Called by exactly one thread at a time: the publisher thread, or the
        // app thread when there is no publisher.
        void PublishFrame(uint32_t slot, uint64_t clientTs, double composeStart, double releaseS, const FrameTraceRow& tr) {
            auto& s = m_ipc.state->shim;
            const double nowS = QpcSeconds();
            s.lastComposeMs = (float)((nowS - composeStart) * 1000.0);
            if (releaseS > 0 && nowS > releaseS) {
                // Running-start lead: envelope of release -> GPU-complete. Fast attack, slow decay (0.1 ms per frame: a long frame
                // is forgotten within about a second), kept within [1 ms, 100 ms].
                const double sample = nowS - releaseS;
                double est = std::max(sample, m_appTimeS.load() - 0.0001);
                est = std::min(std::max(est, 0.001), 0.1);
                m_appTimeS = est;
                s.appFrameMs = (float)(est * 1000.0);
            }
            m_lastPublishedSlot = slot;
            s.slotBusyWaits = (uint32_t)m_slotBusyWaits.load();
            s.releases = m_releases.load();
            s.releasesLate = m_releasesLate.load();
            s.frameSeq++; // odd: the host re-reads until it sees the same even value before and after
            MemoryBarrier();
            s.frameSlot = slot;
            s.frameClientTsNs = clientTs;
            s.frameSubmitTimeS = QpcSeconds();
            s.frameCounter = ++m_framesPublished;
            MemoryBarrier();
            s.frameSeq++; // even: stable
            SetEvent(m_ipc.frame);
#ifdef VISIONALVR_TELEMETRY
            {
                char line[640];
                snprintf(line, sizeof(line), "%llu,%u,%llu,%.6f,%.6f,%.6f,%.6f,%d,%d,%.6f,%.6f,%.6f,%.7f,%.7f,%.7f,%.7f,%.7f,%.7f,%.7f,%.7f,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f\n",
                         (unsigned long long)m_framesPublished, slot, (unsigned long long)clientTs, tr.displayTime, releaseS, tr.tRead, tr.tArrive, tr.predicted,
                         tr.repeated, tr.tSubmit, composeStart, s.frameSubmitTimeS, tr.q[0], tr.q[1], tr.q[2], tr.q[3], tr.sq[0], tr.sq[1], tr.sq[2], tr.sq[3],
                         tr.sp[0], tr.sp[1], tr.sp[2], tr.lp[0], tr.lp[1], tr.lp[2]);
                shimlog::FrameCsv(line);
            }
#endif
        }

        // Ring slot for the next frame: never one the host holds (slotBusyMask: its newest frame, or the one its encoder reads),
        // nor one still in flight on the GPU (publish queue), nor the last one published (the host's intake may take it at any
        // moment). Round-robin otherwise. With every slot excluded (not expected with 6 slots), wait for one, 50 ms at most.
        uint32_t PickSlot() {
            std::unique_lock lock(m_publishMutex);
            for (int attempt = 0; attempt < 26; attempt++) {
                const uint32_t busy = m_ipc.state ? *(volatile uint32_t*)&m_ipc.state->host.slotBusyMask : 0;
                const uint32_t lastPublished = m_lastPublishedSlot.load();
                for (int i = 0; i < visionalvr_ipc::kSlots; i++) {
                    const uint32_t s = (m_nextSlot + i) % visionalvr_ipc::kSlots;
                    if ((busy & (1u << s)) || s == lastPublished) {
                        continue;
                    }
                    bool inFlight = false;
                    for (const auto& p : m_publishQueue) {
                        inFlight |= p.slot == s;
                    }
                    if (inFlight) {
                        continue;
                    }
                    m_nextSlot = (s + 1) % visionalvr_ipc::kSlots;
                    return s;
                }
                if (attempt == 0) {
                    m_slotBusyWaits++;
                }
                m_publishCv.wait_for(lock, std::chrono::milliseconds(2));
            }
            const uint32_t s = m_nextSlot;
            m_nextSlot = (s + 1) % visionalvr_ipc::kSlots;
            return s;
        }

        void StartPublisher() {
            if (!m_ipc.state || !m_sbsWidth || m_publishThread.joinable()) {
                return;
            }
            wchar_t buf[8];
            const DWORD n = GetEnvironmentVariableW(L"OVRSHIM_SYNC_SUBMIT", buf, 8);
            const bool sync = n > 0 && n < 8 && buf[0] == L'1';
            if (!sync && SUCCEEDED(m_submissionDevice.As(&m_dxgiDevice2))) {
                for (auto& e : m_gpuDone) {
                    e = CreateEventW(nullptr, FALSE, FALSE, nullptr);
                }
                m_stopPublisher = false;
                m_publishThread = std::thread([this]() {
                    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
                    for (;;) {
                        PendingFrame f{};
                        {
                            std::unique_lock lock(m_publishMutex);
                            m_publishCv.wait(lock, [&] { return m_stopPublisher || !m_publishQueue.empty(); });
                            if (m_publishQueue.empty()) {
                                return; // stopping and drained
                            }
                            f = m_publishQueue.front();
                        }
                        // same 50 ms cap as the spin wait: a hung GPU must not stall the stream forever
                        WaitForSingleObject(m_gpuDone[f.slot], 50);
                        PublishFrame(f.slot, f.clientTs, f.composeStart, f.releaseS, f.tr);
                        {
                            std::unique_lock lock(m_publishMutex);
                            m_publishQueue.pop_front();
                        }
                        m_publishCv.notify_all();
                    }
                });
            }
            m_ipc.state->shim.submitMode = m_publishThread.joinable() ? 1 : 0;
            ShimLog("submit mode: %s", m_publishThread.joinable() ? "async (publisher thread)" : "sync (GPU wait on the app thread)");
            shimlog::General("INFO", "game start: %s | %u x %u %d-bit @ %.0f Hz | fov L %.2f/%.2f/%.2f/%.2f | encoding gamma %.2f | submit %s | IPC %ls",
                             shimlog::Utf8(shimlog::ExePath()).c_str(), m_sbsWidth, m_sbsHeight, m_ipc.state->host.encodeBits == 8 ? 8 : 10, m_displayRate,
                             m_eyeFov[0].LeftTan, m_eyeFov[0].RightTan, m_eyeFov[0].UpTan, m_eyeFov[0].DownTan, m_encodingGamma,
                             m_publishThread.joinable() ? "async" : "sync", IpcNamespace());
        }

        void StopPublisher() {
            if (m_publishThread.joinable()) {
                {
                    std::unique_lock lock(m_publishMutex);
                    m_stopPublisher = true;
                }
                m_publishCv.notify_all();
                m_publishThread.join();
            }
            for (auto& e : m_gpuDone) {
                if (e) {
                    CloseHandle(e);
                    e = nullptr;
                }
            }
        }

        // Client tracking timestamp to send this frame with: the one of the head pose the projection layer was rendered with.
        // Like ALVR's own SteamVR driver (PoseHistory::GetBestPoseMatch), match the layer's RenderPose against the poses the
        // app read; "the last pose read" is wrong for engines that read the next frame's pose before submitting this one
        // (render-thread pipelining), and the headset then reprojects the frame against the wrong pose.
        uint64_t FrameClientTimestamp(const std::vector<const ovrLayerHeader*>& layers) {
            const ovrLayerEyeFov* eye = nullptr;
            for (const ovrLayerHeader* h : layers) {
                if (h && (h->Type == ovrLayerType_EyeFov || h->Type == ovrLayerType_EyeFovDepth)) {
                    eye = (const ovrLayerEyeFov*)h;
                    break;
                }
            }
            uint64_t ts = m_lastPoseClientTsNs.load();
            bool matched = false;
            if (eye) {
                // eye orientation = head orientation * eye-to-head rotation (identity for ALVR's orthogonal views)
                const OVR::Quatf target =
                    OVR::Quatf(eye->RenderPose[0].Orientation) * OVR::Quatf(m_eyePose[ovrEye_Left].Orientation).Inverted();
                std::lock_guard lock(m_lastHeadMutex);
                auto sample = [&](size_t k) -> const PoseSample& { // k = 0 is the newest
                    return m_poseRing[(m_poseRingHead + kPoseRing - 1 - k) % kPoseRing];
                };
                // Angle between a recorded pose and the layer's, from the vector part of the relative rotation (sin of half the
                // angle): exact down to float rounding (~1e-7 rad). The |dot| = cos(angle / 2) used before cancels near 1 and
                // could not tell apart poses less than ~0.07 degree apart.
                auto angle = [&](const PoseSample& p) {
                    const double pw = p.orientation.w, px = -(double)p.orientation.x, py = -(double)p.orientation.y,
                                 pz = -(double)p.orientation.z; // conjugate of p
                    const double rx = pw * target.x + px * target.w + py * target.z - pz * target.y;
                    const double ry = pw * target.y - px * target.z + py * target.w + pz * target.x;
                    const double rz = pw * target.z + px * target.y - py * target.x + pz * target.w;
                    return 2.0 * std::asin(std::min(1.0, std::sqrt(rx * rx + ry * ry + rz * rz)));
                };
                if (m_poseRingCount && sample(0).clientTsNs < m_lastFrameTsNs) {
                    m_lastFrameTsNs = 0; // the client's clock restarted (new headset session)
                }
                // The pose the app rendered with is the recorded one with the smallest angle; a head that turns back passes the
                // same orientation twice, so never go behind the previous frame's timestamp. Poses within float noise of the best
                // are a tie (a still head): VDXR stamps the layer with the frame's display time (SensorSampleTime), the time the
                // app asked its pose for, so prefer a tie read for that time (pipelined engines read frame N+1's pose before
                // submitting frame N: taking the newest of the ties stamped frame N with N+1's sample, a few pixels of tremble),
                // else the newest. Display time alone is not enough: some engines render with a pose read for another time.
                constexpr double kTie = 2e-6;         // rad (~0.0001 degree)
                constexpr double kMaxAngle = 0.0087;  // rad (~0.5 degree): the app rendered with a pose it read from us
                double best = 1e9;
                for (size_t k = 0; k < m_poseRingCount; k++) {
                    if (sample(k).clientTsNs >= m_lastFrameTsNs) {
                        best = std::min(best, angle(sample(k)));
                    }
                }
                if (best <= kMaxAngle) {
                    size_t pick = kPoseRing, pickTimed = kPoseRing;
                    for (size_t k = 0; k < m_poseRingCount; k++) { // newest first
                        const PoseSample& p = sample(k);
                        if (p.clientTsNs < m_lastFrameTsNs || angle(p) > best + kTie) {
                            continue;
                        }
                        if (pick == kPoseRing) {
                            pick = k;
                        }
                        if (pickTimed == kPoseRing && eye->SensorSampleTime > 0.0 &&
                            std::abs(p.displayTime - eye->SensorSampleTime) < 1e-4) {
                            pickTimed = k;
                        }
                    }
                    if (pickTimed < kPoseRing) {
                        pick = pickTimed;
                        m_tsByDisplayTime++;
                    } else {
                        m_diagNoDisplayTime++;
                    }
                    if (pick < kPoseRing) {
                        ts = sample(pick).clientTsNs;
                        matched = true;
#ifdef VISIONALVR_TELEMETRY // pose-match diagnostics (the 900-frame log line): angle evaluations per recorded pose, every frame
                        uint32_t ties = 0;
                        for (size_t k = 0; k < m_poseRingCount; k++) {
                            if (sample(k).clientTsNs >= m_lastFrameTsNs && angle(sample(k)) <= best + kTie) {
                                ties++;
                            }
                        }
                        m_diagTies[std::min<uint32_t>(ties, 3) - 1]++;
                        m_diagPickIdx[std::min<size_t>(pick, 3)]++;
                        m_diagAngleMdeg.push_back((float)(best * 57295.78));
                        m_diagAgeMs.push_back((float)((sample(0).clientTsNs - sample(pick).clientTsNs) / 1e6));
#endif
                    }
                }
                m_lastFrameTsNs = std::max(m_lastFrameTsNs, ts);
            }
            if (ts != 0 && ts == m_diagPrevTs) {
                m_diagSameTs++;
            }
            m_diagPrevTs = ts;
            if (m_ipc.state) {
                (matched ? m_ipc.state->shim.tsMatched : m_ipc.state->shim.tsFallback)++;
            }
            return ts;
        }

        // Logs the frame's layer layout when it changes (at most every 2 s, 300 lines per process): type, flags, swapchain
        // size/format/array, sub-rect, quad size / FoV. For diagnosing apps whose UI or view lands in the wrong place.
        void LogLayersIfChanged(const std::vector<const ovrLayerHeader*>& layers) {
            if (m_layerLogs >= 300) {
                return;
            }
            std::string sig;
            char b[256];
            std::shared_lock lock(m_swapchainMutex);
            auto sc = [&](void* p) {
                if (!p || !m_swapchains.count(p)) {
                    return std::string("?");
                }
                const auto& d = ((Swapchain*)p)->desc;
                snprintf(b, sizeof(b), "%dx%d fmt%d arr%d ms%d%s", d.Width, d.Height, (int)d.Format, d.ArraySize, d.SampleCount,
                         d.Type == ovrTexture_Cube ? " cube" : "");
                return std::string(b);
            };
            for (const ovrLayerHeader* h : layers) {
                if (!h) {
                    sig += " [null]";
                    continue;
                }
                const auto* l = (const ovrLayer_Union*)h;
                if (h->Type == ovrLayerType_EyeFov || h->Type == ovrLayerType_EyeFovDepth) {
                    const auto& e = l->EyeFov;
                    snprintf(b, sizeof(b), " [eyefov%s f=0x%x L{%s} vp(%d,%d %dx%d) fov(%.2f %.2f %.2f %.2f) R{", h->Type == ovrLayerType_EyeFovDepth ? "+depth" : "",
                             h->Flags, sc(e.ColorTexture[0]).c_str(), e.Viewport[0].Pos.x, e.Viewport[0].Pos.y, e.Viewport[0].Size.w,
                             e.Viewport[0].Size.h, e.Fov[0].LeftTan, e.Fov[0].RightTan, e.Fov[0].UpTan, e.Fov[0].DownTan);
                    sig += b;
                    snprintf(b, sizeof(b), "%s} vp(%d,%d %dx%d)]", e.ColorTexture[1] ? sc(e.ColorTexture[1]).c_str() : "=L", e.Viewport[1].Pos.x,
                             e.Viewport[1].Pos.y, e.Viewport[1].Size.w, e.Viewport[1].Size.h);
                    sig += b;
                } else if (h->Type == ovrLayerType_Quad || h->Type == ovrLayerType_Cylinder) {
                    const auto& q = l->Quad;
                    snprintf(b, sizeof(b), " [%s f=0x%x {%s} vp(%d,%d %dx%d) size(%.2f %.2f) pos(%.2f %.2f %.2f)]",
                             h->Type == ovrLayerType_Quad ? "quad" : "cyl", h->Flags, sc(q.ColorTexture).c_str(), q.Viewport.Pos.x,
                             q.Viewport.Pos.y, q.Viewport.Size.w, q.Viewport.Size.h, q.QuadSize.x, q.QuadSize.y,
                             q.QuadPoseCenter.Position.x, q.QuadPoseCenter.Position.y, q.QuadPoseCenter.Position.z);
                    sig += b;
                } else if (h->Type == ovrLayerType_Cube) {
                    snprintf(b, sizeof(b), " [cube f=0x%x {%s}]", h->Flags, sc(l->Cube.CubeMapTexture).c_str());
                    sig += b;
                } else {
                    snprintf(b, sizeof(b), " [type%d]", (int)h->Type);
                    sig += b;
                }
            }
            const double now = QpcSeconds();
            if (sig != m_lastLayerSig && now - m_lastLayerLogS >= 2.0) {
                m_lastLayerSig = sig;
                m_lastLayerLogS = now;
                m_layerLogs++;
                ShimLog("layers (%zu):%s", layers.size(), sig.c_str());
            }
        }

        void ProcessActionKeys() {
#define ACTION_KEY(label, key, action)                                                                                 \
    static bool wasCtrl##label##Pressed = false;                                                                       \
    const bool isCtrl##label##Pressed = GetAsyncKeyState(key) < 0;                                                     \
    if (!wasCtrl##label##Pressed && isCtrl##label##Pressed) {                                                          \
        action();                                                                                                      \
    }                                                                                                                  \
    wasCtrl##label##Pressed = isCtrl##label##Pressed;

            OVR::Posef transform = OVR::Posef::Identity();
            ACTION_KEY(Reset, 'R', [&] {
                std::unique_lock lock(m_hmdPoseMutex);
                m_hmdPose.ThePose = OVR::Posef::Identity();
            });
            ACTION_KEY(Forward, 'W', [&] { transform.Translation.z = -0.1f; });
            ACTION_KEY(Left, 'A', [&] { transform.Translation.x = -0.1f; });
            ACTION_KEY(Backward, 'S', [&] { transform.Translation.z = 0.1f; });
            ACTION_KEY(Right, 'D', [&] { transform.Translation.x = 0.1f; });
            ACTION_KEY(TurnLeft, 'Q', [&] { transform.Rotation = OVR::Quatf::FastFromRotationVector({0, 0.1f, 0}); });
            ACTION_KEY(TurnRight, 'E', [&] { transform.Rotation = OVR::Quatf::FastFromRotationVector({0, -0.1f, 0}); });
            std::unique_lock lock(m_hmdPoseMutex);
            m_hmdPose.ThePose = OVR::Posef(m_hmdPose.ThePose) * transform;

            std::unique_lock lock2(m_controllerMutex);
            m_controllerButtons.IndexTriggerRaw[0] = GetAsyncKeyState('U') < 0 ? 1.f : 0.f;
            if (GetAsyncKeyState('I') < 0) {
                m_controllerButtons.Buttons |= ovrButton_Enter;
            } else {
                m_controllerButtons.Buttons &= ~ovrButton_Enter;
            }
            m_controllerButtons.IndexTriggerRaw[1] = GetAsyncKeyState('O') < 0 ? 1.f : 0.f;
            if (GetAsyncKeyState('P') < 0) {
                m_controllerButtons.Buttons |= ovrButton_A;
            } else {
                m_controllerButtons.Buttons &= ~ovrButton_A;
            }
            m_controllerPose[0].ThePose = OVR::Posef(m_hmdPose.ThePose) * k_HeadToLeftController;
            m_controllerPose[1].ThePose = OVR::Posef(m_hmdPose.ThePose) * k_HeadToRightController;
        }

        ovrPoseStatef PropagatePose(const ovrPoseStatef& pose, double time) const {
            const float deltaTime = (float)(time - pose.TimeInSeconds);
            if (deltaTime < FLT_EPSILON) {
                return pose;
            }

            ovrPoseStatef predictedPose;

            // Integrate linear velocity over time.
            OVR::Vector3f linearVelocity = pose.LinearVelocity;
            OVR::Vector3f position = pose.ThePose.Position;
            position += (linearVelocity * deltaTime);
            predictedPose.ThePose.Position = position;
            predictedPose.LinearAcceleration = pose.LinearAcceleration;
            predictedPose.LinearVelocity = pose.LinearVelocity;

            // Integrate angular velocity and acceleration over time. ovrPoseStatef velocities are in tracking (world) space (VDXR
            // hands them to OpenXR as base-space velocities); OVR::Quat::TimeIntegrate expects body-frame rates.
            OVR::Quatf orientation = pose.ThePose.Orientation;
            predictedPose.ThePose.Orientation = orientation.TimeIntegrate(
                orientation.InverseRotate(pose.AngularVelocity), orientation.InverseRotate(pose.AngularAcceleration), deltaTime);
            predictedPose.AngularAcceleration = pose.AngularAcceleration;
            predictedPose.AngularVelocity = pose.AngularVelocity;

            predictedPose.TimeInSeconds = time;

            return predictedPose;
        }

        void OpenHost() {
            char exe[MAX_PATH] = {};
            GetModuleFileNameA(nullptr, exe, MAX_PATH);
            if (!OpenIpc(m_ipc)) {
                m_ipc = {}; // run standalone (keyboard-driven, like OVRNull)
                ShimLog("start: %s: host IPC not available (alvr_host not running, or a different IPC version): standalone", exe);
                shimlog::General("WARN", "VisionALVR host not reachable (not running, or a different version): this game will not reach the headset");
                return;
            }
            {
                const std::wstring name = shimlog::ExeName();
                wcsncpy_s(m_ipc.state->shim.appExe, name.c_str(), _TRUNCATE);
            }
            UpdateDebugDir();
            ShimLog("start: %s: host IPC open (%ls namespace, %s), client %s", exe, IpcNamespace(),
                    m_ipc.created ? "created here: the host has not started yet" : "host's objects",
                    m_ipc.state->host.clientConnected ? "connected" : "not connected yet");
            m_ipc.state->shim.shimAlive = 1;
            // Like SteamVR, wait for the headset (streaming client) before presenting a device.
            wchar_t buf[16];
            const DWORD n = GetEnvironmentVariableW(L"OVRSHIM_WAIT_S", buf, 16);
            const double waitS = n ? _wtof(buf) : 20.0;
            const double t0 = QpcSeconds();
            while (!m_ipc.state->host.clientConnected && QpcSeconds() - t0 < waitS) {
                Sleep(50);
            }
            ShimLog("headset %s after %.1f s", m_ipc.state->host.clientConnected ? "ready" : "NOT connected (gave up waiting)", QpcSeconds() - t0);
            if (!m_ipc.state->host.clientConnected) {
                shimlog::General("WARN", "no headset connected after %.0f s: the game starts with default display settings%s", QpcSeconds() - t0,
                                 m_ipc.created ? " (the IPC block was created here: the host was not running, or runs in another namespace/elevation)" : "");
            }
            UpdateDebugDir();
        }

        // initial: also the display rate and recommended resolution (fixed for the life of the app's session). Later reloads
        // (the host re-publishes the config when the headset sends new views, e.g. an IPD change) only take FoV and eye offsets.
        void LoadHostConfig(bool initial = true) {
            if (!m_ipc.state) {
                return;
            }
            const auto& h = m_ipc.state->host;
            m_hostConfigSeq = h.configSeq;
            if (h.encodingGamma > 0.1f && h.encodingGamma < 10.f) {
                m_encodingGamma = h.encodingGamma;
            }
            UpdateOutputGamma();
            if (initial && h.displayRateHz > 1) {
                m_displayRate = (float)h.displayRateHz;
                m_vsyncInterval = 1.0 / m_displayRate;
                m_photonsTime = 1.f / m_displayRate;
            }
            if (initial && h.eyeWidth > 0 && h.eyeHeight > 0) {
                m_recommendedResolution = {h.eyeWidth, h.eyeHeight};
            }
            for (int e = 0; e < 2; e++) {
                if (h.eyeFovTan[e][0] > 0) {
                    m_eyeFov[e].LeftTan = h.eyeFovTan[e][0];
                    m_eyeFov[e].RightTan = h.eyeFovTan[e][1];
                    m_eyeFov[e].UpTan = h.eyeFovTan[e][2];
                    m_eyeFov[e].DownTan = h.eyeFovTan[e][3];
                    m_eyePose[e].Position = {h.eyeOffset[e][0], h.eyeOffset[e][1], h.eyeOffset[e][2]};
                }
            }
        }

        void CreateSbsRing() {
            if (!m_ipc.state || m_sbsWidth) {
                return;
            }
            const auto& h = m_ipc.state->host;
            if (h.eyeWidth <= 0 || h.eyeHeight <= 0) {
                return;
            }
            m_sbsWidth = (uint32_t)h.eyeWidth * 2;
            m_sbsHeight = (uint32_t)h.eyeHeight;
            const DXGI_FORMAT fmt = h.encodeBits == 10 ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;

            D3D11_TEXTURE2D_DESC desc{};
            desc.Width = m_sbsWidth;
            desc.Height = m_sbsHeight;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.Format = fmt;
            desc.SampleDesc.Count = 1;
            desc.Usage = D3D11_USAGE_DEFAULT;
            desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
            for (int i = 0; i < visionalvr_ipc::kSlots; i++) {
                winrt::check_hresult(m_submissionDevice->CreateTexture2D(&desc, nullptr, m_sbsTex[i].ReleaseAndGetAddressOf()));
                winrt::check_hresult(m_submissionDevice->CreateRenderTargetView(
                    m_sbsTex[i].Get(), nullptr, m_sbsRTV[i].ReleaseAndGetAddressOf()));
                ComPtr<IDXGIResource> res;
                winrt::check_hresult(m_sbsTex[i].As(&res));
                HANDLE shared = nullptr;
                winrt::check_hresult(res->GetSharedHandle(&shared));
                m_ipc.state->shim.slotHandle[i] = (uint64_t)(uintptr_t)shared;
            }
            {
                // linear-light FP16 intermediate for multi-layer frames
                D3D11_TEXTURE2D_DESC ld{};
                ld.Width = m_sbsWidth;
                ld.Height = m_sbsHeight;
                ld.MipLevels = 1;
                ld.ArraySize = 1;
                ld.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
                ld.SampleDesc.Count = 1;
                ld.Usage = D3D11_USAGE_DEFAULT;
                ld.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
                winrt::check_hresult(m_submissionDevice->CreateTexture2D(&ld, nullptr, m_linearTex.ReleaseAndGetAddressOf()));
                winrt::check_hresult(m_submissionDevice->CreateRenderTargetView(m_linearTex.Get(), nullptr, m_linearRTV.ReleaseAndGetAddressOf()));
                winrt::check_hresult(m_submissionDevice->CreateShaderResourceView(m_linearTex.Get(), nullptr, m_linearSRV.ReleaseAndGetAddressOf()));
            }
            D3D11_QUERY_DESC qd{D3D11_QUERY_EVENT, 0};
            winrt::check_hresult(m_submissionDevice->CreateQuery(&qd, m_doneQuery.ReleaseAndGetAddressOf()));
            m_ipc.state->shim.sbsWidth = (int32_t)m_sbsWidth;
            m_ipc.state->shim.sbsHeight = (int32_t)m_sbsHeight;
            m_ipc.state->shim.sbsFormat = (uint32_t)fmt;
            ShimLog("ring: encoding gamma %.2f; %u x %u %s-bit, %.2f Hz, fov L(%.3f %.3f %.3f %.3f) R(%.3f %.3f %.3f %.3f) tan", m_encodingGamma, m_sbsWidth, m_sbsHeight,
                    h.encodeBits == 10 ? "10" : "8", m_displayRate, m_eyeFov[0].LeftTan, m_eyeFov[0].RightTan, m_eyeFov[0].UpTan,
                    m_eyeFov[0].DownTan, m_eyeFov[1].LeftTan, m_eyeFov[1].RightTan, m_eyeFov[1].UpTan, m_eyeFov[1].DownTan);
            MemoryBarrier();
            m_ipc.state->shim.frameCounter = 0;
        }

        std::string GetManufacturerName() const override {
            return "Virtual Desktop";
        }

        std::string GetModelNumber() const override {
            return "Emulated Device";
        }

        LUID GetAdapterLuid() const override {
            return m_adapterLuid;
        }

        float GetDisplayRate() const override {
            return m_displayRate;
        }

        ovrSizei GetRecommendedResolution() const override {
            return m_recommendedResolution;
        }

        ovrFovPort GetEyeFov(ovrEyeType eye) const override {
            return m_eyeFov[eye];
        }

        ovrPosef GetEyePose(ovrEyeType eye) const override {
            std::shared_lock lock(m_hmdPoseMutex);
            return m_eyePose[eye];
        }

        void SetStageTracking(bool useStage) override {
            m_useStageTracking = useStage;
        }

        bool IsStageTracking() const override {
            return m_useStageTracking;
        }

        float GetEyeHeight() const override {
            // the user's standing eye height from the host (the launcher's "starting height"): VDXR adds it to the eye-level poses to make the floor-level
            // (STAGE / LOCAL_FLOOR) spaces, so games that think in absolute heights see the player at that height (read every locate: live)
            if (m_ipc.state) {
                const float h = m_ipc.state->host.eyeHeightM;
                if (h > 0.3f && h < 3.0f) {
                    return h;
                }
            }
            return OVR_DEFAULT_EYE_HEIGHT;
        }

        bool IsAswActive() const override {
            return false;
        }

        // Reads the host's latest head pose (seqlock) as an ovrPoseStatef stamped with the shim-clock receive time.
        bool ReadHostHead(ovrPoseStatef& out, uint64_t& clientTsNs) const {
            if (!m_ipc.state) {
                return false;
            }
            const auto& h = m_ipc.state->host;
            for (int attempt = 0; attempt < 8; attempt++) {
                const uint64_t s1 = h.headSeq;
                if (s1 & 1 || s1 == 0) {
                    _mm_pause();
                    continue;
                }
                MemoryBarrier();
                const visionalvr_ipc::Pose p = h.head;
                const double t = h.headHostTimeS;
                const uint64_t ts = h.headClientTsNs;
                float lv[3], av[3];
                memcpy(lv, h.headLinVel, sizeof(lv));
                memcpy(av, h.headAngVel, sizeof(av));
                MemoryBarrier();
                if (h.headSeq != s1) {
                    continue;
                }
                ovrPoseStatef pose{};
                pose.ThePose.Orientation = {p.orientation[0], p.orientation[1], p.orientation[2], p.orientation[3]};
                pose.ThePose.Position = {p.position[0], p.position[1], p.position[2]};
                pose.LinearVelocity = {lv[0], lv[1], lv[2]};
                pose.AngularVelocity = {av[0], av[1], av[2]};
                pose.TimeInSeconds = t;
                out = pose;
                clientTsNs = ts;
                return true;
            }
            return false;
        }

        // Head sample for the next frame. The latest sample is not always the right one: the app is released at some phase
        // relative to the arrival of the client's samples (one per display period), and when that phase drifts across the
        // arrival, two consecutive frames read the same sample and the next one jumps two (duplicate timestamp, then a skip:
        // a missing frame and a repeat on the headset). So samples are consumed in order, one per frame: the oldest one newer
        // than the last consumed; when none is newer the frame waits briefly for the sample to arrive; when the app has fallen
        // two or more samples behind it jumps to the newest but one (catch-up).
        bool SelectHostHead(ovrPoseStatef& out, uint64_t& clientTsNs, double absTime) const {
            if (!m_ipc.state) {
                return false;
            }
            using namespace visionalvr_ipc;
            const auto& h = m_ipc.state->host;
            // one sample per frame, keyed by the display time the app asks the pose for (an app or VDXR reads the head pose several
            // times per frame, and pipelined engines read frame N+1's pose before submitting N): the same time gets the same sample
            for (const auto& e : m_poseCache) {
                if (e.ts != 0 && std::abs(e.absTime - absTime) < 1e-4) {
                    out = e.pose;
                    clientTsNs = e.ts;
                    m_selPredicted = e.predicted;
                    m_selRepeated = e.repeated;
                    return true;
                }
            }
            static const double trackWaitEnv = EnvDouble("OVRSHIM_TRACK_WAIT_MS", -1.0); // lab override of the host setting
            const double trackWaitS = (trackWaitEnv >= 0 ? trackWaitEnv : (h.headWaitMs > 0 ? h.headWaitMs : 3.0)) * 1e-3; // how long a frame waits for its head sample before reusing/predicting
            const double waitUntil = QpcSeconds() + trackWaitS;
            for (;;) {
                HeadSample c[kHeadHist + 1]{}; // newest first
                bool ok = false;
                for (int attempt = 0; attempt < 8 && !ok; attempt++) {
                    const uint64_t s1 = h.headSeq;
                    if (s1 & 1 || s1 == 0) {
                        _mm_pause();
                        continue;
                    }
                    MemoryBarrier();
                    c[0].clientTsNs = h.headClientTsNs;
                    c[0].hostTimeS = h.headHostTimeS;
                    c[0].head = h.head;
                    memcpy(c[0].linVel, h.headLinVel, 12);
                    memcpy(c[0].angVel, h.headAngVel, 12);
                    for (int k = 0; k < kHeadHist; k++) {
                        c[k + 1] = h.headHist[k];
                    }
                    MemoryBarrier();
                    ok = h.headSeq == s1;
                }
                if (!ok) {
                    return false;
                }
                const uint64_t last = m_lastConsumedTs;
                if (c[0].clientTsNs + 200000000ull < last) { // (a predicted timestamp may run a few periods ahead of the real samples)
                    m_lastConsumedTs = 0; // the client's clock restarted
                    m_consumeLagStreak = 0;
                    continue;
                }
                int newer = 0; // samples newer than the last consumed (newest first: c[0..newer-1])
                while (newer < kHeadHist + 1 && c[newer].clientTsNs > last + 1000000ull && c[newer].clientTsNs != 0) { // 1 ms: the real sample for a predicted timestamp is not a new one
                    newer++;
                }
                if (newer == 0 && last != 0 && QpcSeconds() < waitUntil) {
                    Sleep(0); // the sample for this frame is still in flight
                    _mm_pause();
                    continue;
                }
                int pick = 0;
                static const bool predictEnv = EnvSet("OVRSHIM_PREDICT_MISSING");
                const bool predictMissing = predictEnv || h.predictMissingHead > 0.f;
                if (predictMissing && newer == 0 && last != 0 && c[0].clientTsNs != 0 && c[1].clientTsNs != 0 && c[0].clientTsNs > c[1].clientTsNs) {
                    // The sample for this frame is late (uplink): rather than render a second frame with the previous sample (a
                    // duplicate timestamp, then a jump of two when the late ones arrive) stamp the frame with the timestamp the
                    // client's next sample will carry (the client's tick period is regular) and extrapolate the head to it from the
                    // last two samples. The client holds its own pose for that timestamp, whenever the sample reaches us.
                    const double dtc = (double)(c[0].clientTsNs - c[1].clientTsNs);
                    if (dtc > 5e6 && dtc < 20e6) {
                        const uint64_t tsNew = last + (uint64_t)dtc;
                        const double k = (double)(tsNew - c[0].clientTsNs) / dtc;
                        if (k <= 4.0) {
                            const OVR::Quatf q0(c[0].head.orientation[0], c[0].head.orientation[1], c[0].head.orientation[2], c[0].head.orientation[3]);
                            const OVR::Quatf q1(c[1].head.orientation[0], c[1].head.orientation[1], c[1].head.orientation[2], c[1].head.orientation[3]);
                            const OVR::Quatf rel = q0 * q1.Inverted();
                            const float vl = std::sqrt(rel.x * rel.x + rel.y * rel.y + rel.z * rel.z);
                            OVR::Quatf qn = q0;
                            if (vl > 1e-9f) {
                                const float ang = 2.f * std::atan2(vl, rel.w);
                                qn = (OVR::Quatf(OVR::Vector3f(rel.x / vl, rel.y / vl, rel.z / vl), ang * (float)k) * q0).Normalized();
                            }
                            out = {};
                            out.ThePose.Orientation = {qn.x, qn.y, qn.z, qn.w};
                            for (int a = 0; a < 3; a++) {
                                (&out.ThePose.Position.x)[a] = c[0].head.position[a] + (float)k * (c[0].head.position[a] - c[1].head.position[a]);
                            }
                            out.LinearVelocity = {c[0].linVel[0], c[0].linVel[1], c[0].linVel[2]};
                            out.AngularVelocity = {c[0].angVel[0], c[0].angVel[1], c[0].angVel[2]};
                            out.TimeInSeconds = c[0].hostTimeS;
                            clientTsNs = tsNew;
                            m_lastConsumedTs = tsNew;
                            m_consumePredicted++;
                            m_selPredicted = 1;
                            m_selRepeated = 0;
                            m_poseCache[m_poseCacheNext] = {absTime, out, clientTsNs, 1, 0};
                            m_poseCacheNext = (m_poseCacheNext + 1) % 4;
                            return true;
                        }
                    }
                }
                if (last == 0 || newer == 0) {
                    pick = 0;
                    if (newer == 0 && last != 0) {
                        m_consumeRepeats++;
                    }
                } else if (newer >= 3 || (newer == 2 && m_consumeLagStreak >= 2)) {
                    pick = 0; // behind by two or more samples (or lagging steadily): catch up to the newest
                    m_consumeLagStreak = 0;
                    m_consumeSkips++;
                } else {
                    pick = newer - 1; // the oldest unconsumed
                    m_consumeLagStreak = newer == 2 ? m_consumeLagStreak + 1 : 0;
                }
                const HeadSample& sm = c[pick];
                m_lastConsumedTs = std::max<uint64_t>(m_lastConsumedTs, sm.clientTsNs);
                out = {};
                out.ThePose.Orientation = {sm.head.orientation[0], sm.head.orientation[1], sm.head.orientation[2], sm.head.orientation[3]};
                out.ThePose.Position = {sm.head.position[0], sm.head.position[1], sm.head.position[2]};
                out.LinearVelocity = {sm.linVel[0], sm.linVel[1], sm.linVel[2]};
                out.AngularVelocity = {sm.angVel[0], sm.angVel[1], sm.angVel[2]};
                out.TimeInSeconds = sm.hostTimeS;
                clientTsNs = sm.clientTsNs;
                m_selPredicted = 0;
                m_selRepeated = (last != 0 && newer == 0) ? 1 : 0;
                m_poseCache[m_poseCacheNext] = {absTime, out, clientTsNs, m_selPredicted, m_selRepeated};
                m_poseCacheNext = (m_poseCacheNext + 1) % 4;
                return true;
            }
        }

        // Reads one hand from the host under the same seqlock as the head (no torn pose). False if the hand is not tracked.
        bool ReadHostHand(int side, ovrPoseStatef& out) const {
            const auto& h = m_ipc.state->host;
            for (int attempt = 0; attempt < 8; attempt++) {
                const uint64_t s1 = h.headSeq;
                if (s1 & 1) {
                    _mm_pause();
                    continue;
                }
                MemoryBarrier();
                const uint32_t valid = h.handValid[side];
                const visionalvr_ipc::Pose p = h.hand[side];
                float lv[3], av[3];
                memcpy(lv, h.handLinVel[side], sizeof(lv));
                memcpy(av, h.handAngVel[side], sizeof(av));
                const double t = h.headHostTimeS;
                MemoryBarrier();
                if (h.headSeq != s1) {
                    continue;
                }
                if (!valid) {
                    return false;
                }
                out = {};
                out.ThePose.Orientation = {p.orientation[0], p.orientation[1], p.orientation[2], p.orientation[3]};
                out.ThePose.Position = {p.position[0], p.position[1], p.position[2]};
                {
                    // ALVR (OpenVR Touch) -> LibOVR Touch pose convention, see ipc.h handOffsetQuat
                    const float* oq = h.handOffsetQuat[side];
                    if (oq[0] != 0.f || oq[1] != 0.f || oq[2] != 0.f || oq[3] != 0.f) {
                        const OVR::Quatf offQ(oq[0], oq[1], oq[2], oq[3]);
                        const OVR::Vector3f offP(h.handOffsetPos[side][0], h.handOffsetPos[side][1], h.handOffsetPos[side][2]);
                        const OVR::Quatf q = OVR::Quatf(out.ThePose.Orientation) * offQ;
                        out.ThePose.Orientation = q.Normalized();
                        out.ThePose.Position = OVR::Vector3f(out.ThePose.Position) + q.Rotate(offP);
                    }
                }
                out.LinearVelocity = {lv[0], lv[1], lv[2]};
                out.AngularVelocity = {av[0], av[1], av[2]};
                out.TimeInSeconds = t;
                return true;
            }
            return false;
        }

        ovrPoseStatef GetHmdPose(double absTime) const override {
            ovrPoseStatef latched;
            uint64_t clientTs = 0;
            // Default: the latest head sample. In-order consumption with a wait or a predicted timestamp (SelectHostHead) is opt-in
            // (video.predict_missing_head, or OVRSHIM_INORDER_POSE for the lab): against the real headset the "next" sample is often
            // not there yet (20-60 % of frames waited then repeated) and a predicted timestamp matches one of the client's own only
            // 16 % of the time (its timestamps jitter by tens of ns), so the client reprojects with the wrong pose.
            static const bool inOrderEnv = EnvSet("OVRSHIM_INORDER_POSE");
            const bool legacyPose = !(inOrderEnv || (m_ipc.state && m_ipc.state->host.predictMissingHead > 0.f));
            if (legacyPose ? ReadHostHead(latched, clientTs) : SelectHostHead(latched, clientTs, absTime)) {
                // Remember which client tracking sample the app is about to render with: the frame it submits
                // must carry this timestamp so the headset can reproject against the right pose.
                m_lastPoseClientTsNs = clientTs;
            } else {
                std::shared_lock lock(m_hmdPoseMutex);
                latched = m_hmdPose;
            }
            const ovrPoseStatef result = PropagatePose(latched, absTime);
            {
                std::lock_guard lock(m_lastHeadMutex);
#ifdef VISIONALVR_TELEMETRY
                if (clientTs) {
                    m_readRing[m_readNext] = {clientTs, absTime, QpcSeconds(), latched.TimeInSeconds, legacyPose ? 0 : m_selPredicted, legacyPose ? 0 : m_selRepeated,
                        {latched.ThePose.Orientation.x, latched.ThePose.Orientation.y, latched.ThePose.Orientation.z, latched.ThePose.Orientation.w},
                        {latched.ThePose.Position.x, latched.ThePose.Position.y, latched.ThePose.Position.z}};
                    m_readNext = (m_readNext + 1) % 8;
                }
#endif
                m_lastHeadPose = result.ThePose;
                if (clientTs) {
                    // history of what the app read, for FrameClientTimestamp (skip exact repeats of the newest entry)
                    const OVR::Quatf q = result.ThePose.Orientation;
                    const PoseSample* newest = m_poseRingCount ? &m_poseRing[(m_poseRingHead + kPoseRing - 1) % kPoseRing] : nullptr;
                    if (!newest || newest->clientTsNs != clientTs || newest->displayTime != absTime ||
                        std::abs(newest->orientation.Dot(q)) < 0.9999999f) {
                        m_poseRing[m_poseRingHead] = {clientTs, q, absTime};
                        m_poseRingHead = (m_poseRingHead + 1) % kPoseRing;
                        m_poseRingCount = std::min(m_poseRingCount + 1, kPoseRing);
                    }
                }
            }
            return result;
        }

        bool HasController(ovrHandType side) const override {
            std::shared_lock lock(m_controllerMutex);
            return m_controllerSides & (1 << side);
        }

        ovrPoseStatef GetControllerPose(ovrHandType side, double absTime) const override {
            ovrPoseStatef latched;
            const bool tracked = m_ipc.state && ReadHostHand(side, latched); // the headset tracks this controller
            if (tracked) {
                // pose from the host
            } else if (m_ipc.state) {
                // No tracked controller from the headset: hold the controllers at a fixed offset from the head.
                ovrPoseStatef head;
                uint64_t ts;
                if (!ReadHostHead(head, ts)) {
                    head = m_hmdPose;
                }
                latched = head;
                latched.ThePose = OVR::Posef(head.ThePose) * (side == ovrHand_Left ? k_HeadToLeftController : k_HeadToRightController);
            } else {
                std::shared_lock lock(m_controllerMutex);
                latched = m_controllerPose[side];
            }
            return PropagatePose(latched, absTime);
        }

        ovrInputState GetControllerButtons() const override {
            ovrInputState inputState = m_controllerButtons;
            if (m_ipc.state) {
                using namespace visionalvr_ipc;
                const auto& h = m_ipc.state->host;
                unsigned int buttons = 0, touches = 0;
                for (uint32_t side = 0; side < ovrHand_Count; side++) {
                    const HandInput in = h.handInput[side];
                    const bool left = side == ovrHand_Left;
                    if (in.clicks & kClickPrimary) buttons |= left ? ovrButton_X : ovrButton_A;
                    if (in.clicks & kClickSecondary) buttons |= left ? ovrButton_Y : ovrButton_B;
                    if (in.clicks & kClickThumbstick) buttons |= left ? ovrButton_LThumb : ovrButton_RThumb;
                    if ((in.clicks & kClickMenu) && left) buttons |= ovrButton_Enter;
                    if ((in.clicks & kClickSystem) && !left) buttons |= ovrButton_Home;
                    if (in.touches & kTouchPrimary) touches |= left ? ovrTouch_X : ovrTouch_A;
                    if (in.touches & kTouchSecondary) touches |= left ? ovrTouch_Y : ovrTouch_B;
                    if (in.touches & kTouchThumbstick) touches |= left ? ovrTouch_LThumb : ovrTouch_RThumb;
                    if (in.touches & kTouchTrigger) touches |= left ? ovrTouch_LIndexTrigger : ovrTouch_RIndexTrigger;
                    if (in.touches & kTouchThumbrest) touches |= left ? ovrTouch_LThumbRest : ovrTouch_RThumbRest;
                    inputState.IndexTriggerRaw[side] = in.trigger;
                    inputState.HandTriggerRaw[side] = in.squeeze;
                    inputState.ThumbstickRaw[side] = {in.stickX, in.stickY};
                }
                inputState.Buttons = buttons;
                inputState.Touches = touches;
                inputState.TimeInSeconds = QpcSeconds();
            }
            for (uint32_t side = 0; side < ovrHand_Count; side++) {
                inputState.IndexTrigger[side] = inputState.IndexTriggerNoDeadzone[side] =
                    inputState.IndexTriggerRaw[side];
                inputState.HandTrigger[side] = inputState.HandTriggerNoDeadzone[side] = inputState.HandTriggerRaw[side];
                inputState.Thumbstick[side] = inputState.ThumbstickNoDeadzone[side] = inputState.ThumbstickRaw[side];
            }
            return inputState;
        }

        void SetControllerVibration(ovrHandType side, float frequency, float amplitude) override {
            // VDXR re-asserts the current vibration every sync; the host turns changes into ALVR haptic pulses.
            if (m_ipc.state && (side == ovrHand_Left || side == ovrHand_Right)) {
                auto& hap = m_ipc.state->shim.haptic[side];
                hap.frequency = frequency;
                hap.amplitude = amplitude;
                MemoryBarrier();
                hap.seq++;
            }
        }

      private:
        std::thread m_serverThread;
        std::atomic<bool> m_terminateServerThread = false;

        LUID m_adapterLuid{};
        ComPtr<ID3D11Device> m_submissionDevice;
        ComPtr<ID3D11DeviceContext> m_submissionContext;
        ComPtr<ID3D11VertexShader> m_reprojectVS;
        ComPtr<ID3D11PixelShader> m_reprojectPS;
        ComPtr<ID3D11VertexShader> m_layerVS;
        ComPtr<ID3D11PixelShader> m_layerPS;
        ComPtr<ID3D11PixelShader> m_layerCubePS;
        ComPtr<ID3D11Buffer> m_layerConstantsBuffer;
        ComPtr<ID3D11RasterizerState> m_noCullState;
        ComPtr<ID3D11Texture2D> m_linearTex;
        ComPtr<ID3D11RenderTargetView> m_linearRTV;
        ComPtr<ID3D11ShaderResourceView> m_linearSRV;
        mutable std::mutex m_lastHeadMutex;
        mutable ovrPosef m_lastHeadPose = OVR::Posef::Identity();
        ComPtr<ID3D11Buffer> m_constantsBuffer;
        ComPtr<ID3D11DepthStencilState> m_noDepthTestState;
        ComPtr<ID3D11BlendState> m_blendState;
        ComPtr<ID3D11SamplerState> m_linearSampler;

        float m_displayRate{};
        float m_photonsTime{0};
        float m_outputGamma{1.f};   // exponent written into the stream: 1 / (encoding gamma * user gamma)
        float m_encodingGamma{1.f}; // negotiated ALVR encoding gamma
        uint32_t m_hostConfigSeq = 0;
        ovrSizei m_recommendedResolution{};
        ovrFovPort m_eyeFov[ovrEye_Count]{};
        bool m_useStageTracking{false};

        mutable std::shared_mutex m_swapchainMutex;
        std::unordered_set<void*> m_swapchains;
        ComPtr<ID3D11RenderTargetView> m_mirrorRTV;
        uint32_t m_mirrorWidth{0};
        uint32_t m_mirrorHeight{0};

        mutable std::shared_mutex m_controllerMutex;
        uint32_t m_controllerSides{0};
        ovrPoseStatef m_controllerPose[ovrHand_Count]{{OVR::Posef::Identity()}, {OVR::Posef::Identity()}};
        ovrInputState m_controllerButtons{};

        mutable std::shared_mutex m_hmdPoseMutex;
        ovrPoseStatef m_hmdPose{OVR::Posef::Identity()};
        ovrPosef m_eyePose[ovrEye_Count]{OVR::Posef::Identity(), OVR::Posef::Identity()};

        mutable std::mutex m_frameMutex;
        long long m_lastSignaledVsync{};
        double m_prevHostTickS{0.0};
        bool m_phaseFresh{true};
        mutable int m_gpdtTrace{0};
        mutable std::atomic<uint64_t> m_lastConsumedTs{0};
        mutable int m_consumeLagStreak{0};
        struct PoseCacheEntry { double absTime; ovrPoseStatef pose; uint64_t ts; int predicted; int repeated; };
        struct PoseRead { uint64_t ts; double absTime, tRead, tArrive; int predicted, repeated; float sq[4], sp[3]; };
        mutable PoseRead m_readRing[8]{};
        mutable int m_readNext{0};
        mutable int m_selPredicted{0}, m_selRepeated{0};
        mutable PoseCacheEntry m_poseCache[4]{};
        mutable int m_poseCacheNext{0};
        mutable uint64_t m_consumeRepeats{0}, m_consumeSkips{0}, m_consumePredicted{0};
        mutable int m_hmdTrace{0};
        mutable uint64_t m_hmdTraceLastTs{0};
        mutable std::mutex m_displayMutex;
        std::atomic<bool> m_rsDisplayTimeOwned{false};
        uint64_t m_vsyncCoalesced{0};
        long long m_lastWaitedFrame{};
        std::atomic<bool> m_submittedSinceWait{false};
        uint64_t m_waitCalls{0}, m_waitWaited{0}, m_waitRepeatedIndex{0}, m_waitTimeouts{0};
        double m_statT0{0};
        long long m_statTicks0{0};
        std::atomic<uint32_t> m_vsyncFromHost{0}, m_vsyncTimeouts{0};
        std::string m_lastLayerSig;
        double m_lastLayerLogS{0};
        int m_layerLogs{0};
        double m_nextFramePredictedDisplayTime{0.0};
        double m_vsyncInterval{1.0 / 72.0}; // latest tick-to-tick interval (host-scheduled), nominal until the host reports one
        std::condition_variable m_frameVsync;

        // Host IPC and the composed side-by-side frame ring.
        visionalvr_ipc::Ipc m_ipc{};
        mutable std::atomic<uint64_t> m_lastPoseClientTsNs{0};
        uint64_t m_framesPublished{0}; // frames handed to the host (frameCounter)
        uint64_t m_framesSubmitted{0}; // frames composed (picks the ring slot)
        // Running start (see WaitForVsync): the app is released appTime + margin before the host's next compositor boundary.
        std::atomic<double> m_lastReleaseS{0.0};   // shim clock when WaitForVsync last released the app
        std::atomic<double> m_appTimeS{0.004};     // envelope of release -> frame GPU-complete (published), seconds
        long long m_lastTargetBoundary{0};         // boundary (tick index) the last release aimed at: one release per boundary
        double m_latestTickS{0.0};                 // shim-clock time of the host's latest display tick (under m_frameMutex)
        std::atomic<uint64_t> m_releases{0}, m_releasesLate{0}, m_slotBusyWaits{0};
        uint64_t m_gpuThrottleTimeouts{0};
        uint32_t m_nextSlot{0};                    // round-robin position of PickSlot
        std::atomic<uint32_t> m_lastPublishedSlot{0xFFFFFFFFu};
        // head poses the app read, with their client timestamps (guarded by m_lastHeadMutex)
        struct PoseSample {
            uint64_t clientTsNs;
            OVR::Quatf orientation;
            double displayTime; // the absTime the app asked the pose for (its frame's predicted display time)
        };
        uint64_t m_tsByDisplayTime{0}; // frames matched through SensorSampleTime (FrameClientTimestamp)
        // matching diagnostics since the last stats line: residual angle of the match (millidegrees), which sample was picked
        // (0 = the newest the app had read), its age behind the newest, and how many recorded poses tied with it
        std::vector<float> m_diagAngleMdeg, m_diagAgeMs;
        uint32_t m_diagPickIdx[4]{};   // picked sample index 0, 1, 2, 3+
        uint32_t m_diagTies[3]{};      // 1, 2, 3+ poses within the tie window
        uint32_t m_diagNoDisplayTime{0}; // no recorded pose for the frame's display time
        uint32_t m_diagSameTs{0};        // frames stamped with the previous frame's timestamp (the client may skip those)
        uint64_t m_diagPrevTs{0};
        static constexpr size_t kPoseRing = 256;
        mutable PoseSample m_poseRing[kPoseRing]{};
        mutable size_t m_poseRingHead{0};
        mutable size_t m_poseRingCount{0};
        uint64_t m_lastFrameTsNs{0}; // timestamp of the previous frame (frames never go back in time)
        // async publish: frames composed but not yet GPU-complete, oldest first
        struct PendingFrame {
            uint32_t slot;
            uint64_t clientTs;
            double composeStart;
            double releaseS; // when WaitForVsync released the app for this frame (running-start envelope)
            FrameTraceRow tr;
        };
        ComPtr<IDXGIDevice2> m_dxgiDevice2;
        HANDLE m_gpuDone[visionalvr_ipc::kSlots]{};
        std::thread m_publishThread;
        std::mutex m_publishMutex;
        std::condition_variable m_publishCv;
        std::deque<PendingFrame> m_publishQueue;
        bool m_stopPublisher{false};
        uint32_t m_sbsWidth{0};
        uint32_t m_sbsHeight{0};
        ComPtr<ID3D11Texture2D> m_sbsTex[visionalvr_ipc::kSlots];
        ComPtr<ID3D11RenderTargetView> m_sbsRTV[visionalvr_ipc::kSlots];
        ComPtr<ID3D11Query> m_doneQuery;
    };

} // namespace

namespace ovrnull::driver {

    IDriver* CreateDriver() {
        return new HostDriver();
    }

} // namespace ovrnull::driver

// Audio endpoints. VDXR answers XR_OCULUS_audio_device_guid with ovr_GetAudioDeviceOutGuidStr. Unity's OpenXR plugin calls
// it every frame and looks the string up among the Windows endpoints; OVRNull's stub returned an empty string (and zeroed
// only sizeof(pointer) bytes), the lookup failed every frame ("[XR] GetAduioDeviceGUIDFromIDString Failed.") and its
// failure path corrupted the heap after a while (Unity games crashing in UnityOpenXR -> MMDevAPI -> PropVariantClear).
// The headset's audio is ALVR's capture of the Windows default output, so that endpoint is the right answer; the default
// input is the one ALVR's microphone feeds. stage11 removes OVRNull's versions of these two functions.
namespace {
    struct EndpointIdCache {
        std::mutex mutex;
        std::wstring id;
        ULONGLONG at{0};
    };

    // IMMDevice::GetId of the default endpoint for `flow`, refreshed at most once a second (callers poll every frame).
    std::wstring DefaultEndpointId(EDataFlow flow) {
        static EndpointIdCache caches[2];
        auto& c = caches[flow == eCapture ? 1 : 0];
        std::lock_guard lock(c.mutex);
        const ULONGLONG now = GetTickCount64();
        if (c.at != 0 && now - c.at < 1000) {
            return c.id;
        }
        c.at = now;
        const HRESULT init = CoInitializeEx(nullptr, COINIT_MULTITHREADED); // RPC_E_CHANGED_MODE: the thread already has COM
        std::wstring id;
        {
            ComPtr<IMMDeviceEnumerator> enumerator;
            ComPtr<IMMDevice> device;
            LPWSTR raw = nullptr;
            if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator))) &&
                SUCCEEDED(enumerator->GetDefaultAudioEndpoint(flow, eConsole, &device)) && SUCCEEDED(device->GetId(&raw))) {
                id = raw;
                CoTaskMemFree(raw);
            }
        }
        if (SUCCEEDED(init)) {
            CoUninitialize();
        }
        if (id != c.id) {
            shimlog::Debug("audio: default %s endpoint %s", flow == eCapture ? "input" : "output",
                           id.empty() ? "(none)" : shimlog::Utf8(id).c_str());
        }
        c.id = id;
        return id;
    }

    ovrResult CopyEndpointId(EDataFlow flow, WCHAR* buffer) {
        if (!buffer) {
            return ovrError_InvalidParameter;
        }
        ZeroMemory(buffer, OVR_AUDIO_MAX_DEVICE_STR_SIZE * sizeof(WCHAR));
        const std::wstring id = DefaultEndpointId(flow);
        // no endpoint: an empty string, as before (an error would make VDXR fail the OpenXR call every frame)
        if (!id.empty() && id.size() < OVR_AUDIO_MAX_DEVICE_STR_SIZE) {
            wcsncpy_s(buffer, OVR_AUDIO_MAX_DEVICE_STR_SIZE, id.c_str(), _TRUNCATE);
        }
        return ovrSuccess;
    }
} // namespace

OVR_PUBLIC_FUNCTION(ovrResult) ovr_GetAudioDeviceOutGuidStr(WCHAR deviceOutStrBuffer[OVR_AUDIO_MAX_DEVICE_STR_SIZE]) {
    return CopyEndpointId(eRender, deviceOutStrBuffer);
}

OVR_PUBLIC_FUNCTION(ovrResult) ovr_GetAudioDeviceInGuidStr(WCHAR deviceInStrBuffer[OVR_AUDIO_MAX_DEVICE_STR_SIZE]) {
    return CopyEndpointId(eCapture, deviceInStrBuffer);
}
