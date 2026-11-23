// tvtest2.cpp - plugin VST2 minimo (ABI classica) para testar o hospedeiro do TakaVox. NAO vai no instalador.
// Ganho 0..2x em estereo, janela Win32 com slider, pedido de redimensionamento (audioMasterSizeWindow),
// estado por "chunk" e um modo que provoca um acesso invalido de proposito (ganho exatamente 0,123).
#include <windows.h>
#include <commctrl.h>
#include <cstdint>
#include <cstdio>
#include <cstring>

struct AEffect;
typedef intptr_t (__cdecl *HostCb)(AEffect*, int32_t, int32_t, intptr_t, void*, float);
struct AEffect
{
    int32_t magic;
    intptr_t (__cdecl *dispatcher)(AEffect*, int32_t, int32_t, intptr_t, void*, float);
    void (__cdecl *process)(AEffect*, float**, float**, int32_t);
    void (__cdecl *setParameter)(AEffect*, int32_t, float);
    float (__cdecl *getParameter)(AEffect*, int32_t);
    int32_t numPrograms, numParams, numInputs, numOutputs, flags;
    intptr_t resvd1, resvd2;
    int32_t initialDelay, realQualities, offQualities;
    float ioRatio;
    void* object;
    void* user;
    int32_t uniqueID, version;
    void (__cdecl *processReplacing)(AEffect*, float**, float**, int32_t);
    void (__cdecl *processDoubleReplacing)(AEffect*, double**, double**, int32_t);
    char future[56];
};
struct ERect { int16_t top, left, bottom, right; };

extern "C" IMAGE_DOS_HEADER __ImageBase;

struct Plug
{
    AEffect e;
    HostCb host = nullptr;
    float gain = 0.5f;
    float chunk = 0;
    HWND wnd = nullptr, slider = nullptr;
    ERect rect{0, 0, 150, 380};
};

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    auto* p = (Plug*)GetWindowLongPtrW(h, GWLP_USERDATA);
    if (m == WM_HSCROLL && p && (HWND)l == p->slider)
    {
        p->gain = (float)SendMessageW(p->slider, TBM_GETPOS, 0, 0) / 100.0f;
        p->host(&p->e, 0 /* audioMasterAutomate */, 0, 0, nullptr, p->gain);
        return 0;
    }
    return DefWindowProcW(h, m, w, l);
}

static intptr_t __cdecl Dispatch(AEffect* e, int32_t op, int32_t idx, intptr_t val, void* ptr, float)
{
    Plug* p = (Plug*)e->object;
    switch (op)
    {
    case 1: // effClose
        if (p->wnd) DestroyWindow(p->wnd);
        delete p;
        return 1;
    case 6: strcpy((char*)ptr, "x"); return 1;                      // effGetParamLabel
    case 7: sprintf((char*)ptr, "%.2f", p->gain * 2); return 1;     // effGetParamDisplay
    case 8: strcpy((char*)ptr, "Ganho"); return 1;                  // effGetParamName
    case 13: *(ERect**)ptr = &p->rect; return 1;                    // effEditGetRect
    case 14:                                                        // effEditOpen
    {
        HINSTANCE hi = (HINSTANCE)&__ImageBase;
        static bool reg = false;
        if (!reg)
        {
            WNDCLASSW wc{};
            wc.lpfnWndProc = WndProc;
            wc.hInstance = hi;
            wc.lpszClassName = L"TVTest2Editor";
            wc.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1);
            wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
            RegisterClassW(&wc);
            INITCOMMONCONTROLSEX icc{sizeof(icc), ICC_BAR_CLASSES};
            InitCommonControlsEx(&icc);
            reg = true;
        }
        p->wnd = CreateWindowExW(0, L"TVTest2Editor", L"", WS_CHILD | WS_VISIBLE, 0, 0, 380, 150, (HWND)ptr, nullptr, hi, nullptr);
        SetWindowLongPtrW(p->wnd, GWLP_USERDATA, (LONG_PTR)p);
        CreateWindowExW(0, L"STATIC", L"TV Teste VST2", WS_CHILD | WS_VISIBLE, 10, 10, 200, 20, p->wnd, nullptr, hi, nullptr);
        p->slider = CreateWindowExW(0, TRACKBAR_CLASSW, L"", WS_CHILD | WS_VISIBLE | TBS_HORZ, 10, 40, 360, 30, p->wnd, (HMENU)101, hi, nullptr);
        SendMessageW(p->slider, TBM_SETRANGE, TRUE, MAKELPARAM(0, 100));
        SendMessageW(p->slider, TBM_SETPOS, TRUE, (LPARAM)(p->gain * 100));
        p->rect.right = 400;
        p->rect.bottom = 170;
        SetWindowPos(p->wnd, nullptr, 0, 0, 400, 170, SWP_NOZORDER | SWP_NOMOVE);
        p->host(&p->e, 15 /* audioMasterSizeWindow */, 400, 170, nullptr, 0);
        return 1;
    }
    case 15:                                                        // effEditClose
        if (p->wnd) DestroyWindow(p->wnd);
        p->wnd = p->slider = nullptr;
        return 1;
    case 23: p->chunk = p->gain; *(void**)ptr = &p->chunk; return 4; // effGetChunk
    case 24: if (val >= 4) memcpy(&p->gain, ptr, 4); return 1;       // effSetChunk
    case 45: strcpy((char*)ptr, "TV Teste VST2"); return 1;          // effGetEffectName
    case 47: strcpy((char*)ptr, "TakaVox Teste"); return 1;          // effGetVendorString
    case 58: return 2400;                                            // effGetVstVersion
    default: return 0;
    }
}

static void __cdecl ProcRep(AEffect* e, float** in, float** out, int32_t n)
{
    Plug* p = (Plug*)e->object;
    if (p->gain == 0.123f) *(volatile int*)nullptr = 42; // de proposito: o host tem que sobreviver
    float g = p->gain * 2;
    for (int c = 0; c < 2; c++)
        for (int i = 0; i < n; i++) out[c][i] = in[c][i] * g;
}
static void __cdecl SetParam(AEffect* e, int32_t, float v) { ((Plug*)e->object)->gain = v; }
static float __cdecl GetParam(AEffect* e, int32_t) { return ((Plug*)e->object)->gain; }

// Copias com "trava" ou "demora" no nome simulam plugins ruins para testar o leitor em processo separado.
static void TestMode()
{
    wchar_t path[MAX_PATH] = {0};
    GetModuleFileNameW((HMODULE)&__ImageBase, path, MAX_PATH);
    _wcslwr_s(path);
    if (wcsstr(path, L"trava")) TerminateProcess(GetCurrentProcess(), 99); // derruba o processo inteiro
    if (wcsstr(path, L"demora")) Sleep(INFINITE);                          // congela para sempre
}

extern "C" __declspec(dllexport) AEffect* VSTPluginMain(HostCb host)
{
    TestMode();
    if (!host || host(nullptr, 1 /* audioMasterVersion */, 0, 0, nullptr, 0) == 0) return nullptr;
    Plug* p = new Plug();
    memset(&p->e, 0, sizeof(AEffect));
    p->e.magic = 0x56737450;
    p->e.dispatcher = Dispatch;
    p->e.process = ProcRep;
    p->e.setParameter = SetParam;
    p->e.getParameter = GetParam;
    p->e.numPrograms = 1;
    p->e.numParams = 1;
    p->e.numInputs = 2;
    p->e.numOutputs = 2;
    p->e.flags = 1 | (1 << 4) | (1 << 5); // editor, processReplacing, chunks
    p->e.object = p;
    p->e.uniqueID = 0x54765432;
    p->e.version = 1;
    p->e.processReplacing = ProcRep;
    p->host = host;
    return &p->e;
}
