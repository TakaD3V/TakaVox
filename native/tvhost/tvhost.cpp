// tvhost.dll - hospedeiro de plugins de audio (VST3 e VST2) do TakaVox.
// API C simples para o app em C#. VST3 usa o SDK oficial da Steinberg (licenca MIT); VST2 usa a ABI classica.
// Threads: carregar, editor, parametros, estado, tvh_idle e tvh_reactivate -> thread da interface;
//          tvh_process -> thread de audio.
// Edicoes feitas na janela do plugin vao para o audio por uma fila sem trava, e o que o plugin publica
// de volta (medidores, parametros de saida) volta para a janela pelo tvh_idle.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <dwmapi.h>
#include <xmmintrin.h>
#include <algorithm>
#include <atomic>
#include <cstring>
#include <map>
#include <string>
#include <vector>

#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/common/memorystream.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "pluginterfaces/vst/ivstunits.h"
#include "pluginterfaces/vst/vstspeaker.h"
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/gui/iplugviewcontentscalesupport.h"

#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")

using namespace Steinberg;
using namespace Steinberg::Vst;

#define TVH_API extern "C" __declspec(dllexport)

// ============================================================================ VST2 (ABI classica)
struct AEffect;
typedef intptr_t (__cdecl *Vst2HostCallback)(AEffect*, int32_t, int32_t, intptr_t, void*, float);
typedef AEffect* (__cdecl *Vst2Entry)(Vst2HostCallback);
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
struct VstTimeInfo
{
    double samplePos, sampleRate, nanoSeconds, ppqPos, tempo, barStartPos, cycleStartPos, cycleEndPos;
    int32_t timeSigNumerator, timeSigDenominator, smpteOffset, smpteFrameRate, samplesToNextClock, flags;
};
enum
{
    effOpen = 0, effClose = 1, effGetParamLabel = 6, effGetParamDisplay = 7, effGetParamName = 8,
    effSetProgram = 2, effGetProgram = 3, effGetProgramName = 5, effGetProgramNameIndexed = 29,
    effBeginSetProgram = 67, effEndSetProgram = 68,
    effSetSampleRate = 10, effSetBlockSize = 11, effMainsChanged = 12, effEditGetRect = 13, effEditOpen = 14,
    effEditClose = 15, effEditIdle = 19, effGetChunk = 23, effSetChunk = 24, effGetEffectName = 45,
    effGetVendorString = 47, effGetProductString = 48, effStartProcess = 71, effStopProcess = 72,
};
const int32_t kVstMagic = 0x56737450; // 'VstP'
const int32_t effFlagsHasEditor = 1, effFlagsCanReplacing = 1 << 4, effFlagsProgramChunks = 1 << 5, effFlagsIsSynth = 1 << 8;

// ============================================================================ utilitarios
static std::string ToUtf8(const std::wstring& w)
{
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr, nullptr);
    return s;
}
static std::wstring FromUtf8(const std::string& s)
{
    if (s.empty()) return {};
    // mensagens do SDK podem vir do Windows em ANSI: se nao for UTF-8 valido, le como ANSI
    UINT cp = CP_UTF8;
    DWORD flags = MB_ERR_INVALID_CHARS;
    int n = MultiByteToWideChar(cp, flags, s.c_str(), (int)s.size(), nullptr, 0);
    if (n <= 0) { cp = CP_ACP; flags = 0; n = MultiByteToWideChar(cp, flags, s.c_str(), (int)s.size(), nullptr, 0); }
    if (n <= 0) return {};
    std::wstring w(n, L'\0');
    MultiByteToWideChar(cp, flags, s.c_str(), (int)s.size(), &w[0], n);
    return w;
}
static std::wstring FromAnsi(const char* s)
{
    wchar_t buf[512];
    if (!s || MultiByteToWideChar(CP_ACP, 0, s, -1, buf, 512) <= 0) return {};
    buf[511] = 0;
    return buf;
}
static std::wstring FromString128(const String128 s)
{
    std::wstring w;
    for (int i = 0; i < 128 && s[i]; i++) w.push_back((wchar_t)s[i]);
    return w;
}
static void CopyW(wchar_t* dst, int cap, const std::wstring& src)
{
    if (!dst || cap <= 0) return;
    wcsncpy_s(dst, cap, src.c_str(), _TRUNCATE);
}
static void CopyW(wchar_t* dst, int cap, const std::string& utf8) { CopyW(dst, cap, FromUtf8(utf8)); }
static void CopyW(wchar_t* dst, int cap, const char* utf8) { CopyW(dst, cap, FromUtf8(utf8 ? utf8 : "")); }

static std::wstring TrimSlash(std::wstring s)
{
    while (!s.empty() && (s.back() == L'\\' || s.back() == L'/')) s.pop_back();
    return s;
}
static bool IsVst3Path(const wchar_t* p)
{
    std::wstring s = TrimSlash(p);
    return s.size() > 5 && _wcsicmp(s.c_str() + s.size() - 5, L".vst3") == 0;
}

// Tipo de processador do binario: 0x8664 = x64, 0x14c = x86 (32 bits), 0xAA64 = ARM64, 0 = nao sei.
static int PeMachine(const std::wstring& file)
{
    HANDLE f = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return 0;
    int machine = 0;
    IMAGE_DOS_HEADER dos{};
    DWORD rd = 0;
    if (ReadFile(f, &dos, sizeof(dos), &rd, nullptr) && rd == sizeof(dos) && dos.e_magic == IMAGE_DOS_SIGNATURE &&
        SetFilePointer(f, dos.e_lfanew, nullptr, FILE_BEGIN) != INVALID_SET_FILE_POINTER)
    {
        DWORD sig = 0;
        IMAGE_FILE_HEADER fh{};
        if (ReadFile(f, &sig, 4, &rd, nullptr) && sig == IMAGE_NT_SIGNATURE && ReadFile(f, &fh, sizeof(fh), &rd, nullptr) && rd == sizeof(fh))
            machine = fh.Machine;
    }
    CloseHandle(f);
    return machine;
}
static std::wstring Vst3Binary(const std::wstring& raw)
{
    std::wstring path = TrimSlash(raw);
    DWORD a = GetFileAttributesW(path.c_str());
    if (a == INVALID_FILE_ATTRIBUTES) return L"";
    if (!(a & FILE_ATTRIBUTE_DIRECTORY)) return path;
    size_t sl = path.find_last_of(L"\\/");
    std::wstring name = path.substr(sl == std::wstring::npos ? 0 : sl + 1);
    std::wstring p64 = path + L"\\Contents\\x86_64-win\\" + name;
    if (GetFileAttributesW(p64.c_str()) != INVALID_FILE_ATTRIBUTES) return p64;
    std::wstring p32 = path + L"\\Contents\\x86-win\\" + name;
    if (GetFileAttributesW(p32.c_str()) != INVALID_FILE_ATTRIBUTES) return p32;
    return L"";
}
static bool CheckArch(const wchar_t* path, bool vst3, std::string& err)
{
    if (GetFileAttributesW(TrimSlash(path).c_str()) == INVALID_FILE_ATTRIBUTES) { err = "arquivo não encontrado (o plugin foi apagado ou movido?)"; return false; }
    std::wstring bin = vst3 ? Vst3Binary(path) : std::wstring(path);
    if (bin.empty()) return true; // o SDK explica melhor
    int m = PeMachine(bin);
    if (m == 0x14c) { err = "plugin de 32 bits: o TakaVox é 64 bits e só abre plugins de 64 bits"; return false; }
    if (m == 0xAA64) { err = "plugin feito para processador ARM"; return false; }
    return true;
}

// ---------------------------------------------------------------------------- host global
class TvHostApp : public HostApplication
{
public:
    tresult PLUGIN_API getName(String128 name) override
    {
        static const char16_t kName[] = u"TakaVox";
        for (int i = 0; i < 8; i++) name[i] = kName[i];
        return kResultTrue;
    }
};

static TvHostApp* gHost = nullptr;
static void EnsureHost()
{
    if (!gHost)
    {
        gHost = new TvHostApp();
        PluginContextFactory::instance().setPluginContext(gHost);
    }
}

// Modulos ficam carregados ate o app fechar (descarregar DLL de plugin e fonte classica de travamento).
// Alocados com new e nunca apagados de proposito: nada de FreeLibrary dentro do DllMain na saida.
static std::map<std::wstring, VST3::Hosting::Module::Ptr>* gVst3Modules = nullptr;
static std::map<std::wstring, HMODULE>* gVst2Modules = nullptr;

static std::wstring KeyOf(const wchar_t* path)
{
    std::wstring k = TrimSlash(path);
    for (auto& c : k) c = (wchar_t)towlower(c);
    return k;
}
static VST3::Hosting::Module::Ptr GetVst3Module(const wchar_t* path, std::string& err)
{
    if (!gVst3Modules) gVst3Modules = new std::map<std::wstring, VST3::Hosting::Module::Ptr>();
    std::wstring key = KeyOf(path);
    auto it = gVst3Modules->find(key);
    if (it != gVst3Modules->end()) return it->second;
    auto mod = VST3::Hosting::Module::create(ToUtf8(TrimSlash(path)), err);
    if (mod) (*gVst3Modules)[key] = mod;
    return mod;
}
static HMODULE GetVst2Module(const wchar_t* path)
{
    if (!gVst2Modules) gVst2Modules = new std::map<std::wstring, HMODULE>();
    std::wstring key = KeyOf(path);
    auto it = gVst2Modules->find(key);
    if (it != gVst2Modules->end()) return it->second;
    HMODULE h = LoadLibraryExW(path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (h) (*gVst2Modules)[key] = h;
    return h;
}

// ---------------------------------------------------------------------------- filas sem trava (1 produtor, 1 consumidor)
struct ParamChange { ParamID id; double value; };
struct ParamQueue
{
    static const int N = 1024;
    ParamChange items[N];
    std::atomic<int> head{0}, tail{0};
    void Push(ParamID id, double v)
    {
        int h = head.load(std::memory_order_relaxed), next = (h + 1) % N;
        if (next == tail.load(std::memory_order_acquire)) return; // cheia: descarta
        items[h] = {id, v};
        head.store(next, std::memory_order_release);
    }
    bool Pop(ParamChange& out)
    {
        int t = tail.load(std::memory_order_relaxed);
        if (t == head.load(std::memory_order_acquire)) return false;
        out = items[t];
        tail.store((t + 1) % N, std::memory_order_release);
        return true;
    }
};

struct Instance;

// ---------------------------------------------------------------------------- objetos que o host VST3 entrega ao plugin
class ComponentHandler : public IComponentHandler
{
public:
    Instance* inst = nullptr;
    tresult PLUGIN_API beginEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API performEdit(ParamID id, ParamValue v) override;
    tresult PLUGIN_API endEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API restartComponent(int32 flags) override;
    tresult PLUGIN_API queryInterface(const TUID _iid, void** obj) override
    {
        QUERY_INTERFACE(_iid, obj, FUnknown::iid, IComponentHandler)
        QUERY_INTERFACE(_iid, obj, IComponentHandler::iid, IComponentHandler)
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1; }
    uint32 PLUGIN_API release() override { return 1; }
};

class PlugFrame : public IPlugFrame
{
public:
    Instance* inst = nullptr;
    tresult PLUGIN_API resizeView(IPlugView* view, ViewRect* newSize) override;
    tresult PLUGIN_API queryInterface(const TUID _iid, void** obj) override
    {
        QUERY_INTERFACE(_iid, obj, FUnknown::iid, IPlugFrame)
        QUERY_INTERFACE(_iid, obj, IPlugFrame::iid, IPlugFrame)
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1; }
    uint32 PLUGIN_API release() override { return 1; }
};

// ============================================================================ instancia
enum { kPendLatency = 1, kPendParams = 2, kPendRestart = 4 };

struct Instance
{
    int format = 0; // 3 = VST3, 2 = VST2
    std::wstring name;
    double sr = 48000;
    int maxBlock = 480;
    std::atomic<bool> failed{false};
    std::atomic<int> pending{0};
    int latency = 0;
    int inCh = 0, outCh = 0;

    // VST3
    VST3::Hosting::Module::Ptr module;
    IPtr<PlugProvider> provider;
    IPtr<IComponent> component;
    IPtr<IAudioProcessor> processor;
    IPtr<IEditController> controller;
    HostProcessData data;
    ParameterChanges changes, outChanges;
    EventList evIn, evOut;
    ProcessContext ctx{};
    ComponentHandler handler;
    PlugFrame frame;
    ParamQueue queue;    // UI -> audio
    ParamQueue outQueue; // audio -> UI
    IPtr<IPlugView> view;
    int hasEditor = -1;

    // VST2
    HMODULE dll = nullptr;
    AEffect* eff = nullptr;
    std::vector<std::vector<float>> v2in, v2out;
    std::vector<float*> v2inPtr, v2outPtr;
    VstTimeInfo timeInfo{};

    // janela do editor: separada (editorWnd) ou embutida na moldura do TakaVox (hostWnd, filha da janela WPF)
    HWND editorWnd = nullptr;
    HWND hostWnd = nullptr;
    bool resizable = false, inResize = false;

    // programas (presets de fabrica): calculado uma vez
    bool progKnown = false;
    int32 progCount = 0;
    ProgramListID progList = kNoProgramListId;
    ParamID progParam = 0;
    bool progHasParam = false;

    void ResizeEditor(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        if (hostWnd)
        {
            // embutida: a janela filha muda na hora (o plugin ja desenha no tamanho novo) e a moldura acompanha
            inResize = true;
            SetWindowPos(hostWnd, nullptr, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            inResize = false;
            PostMessageW(hostWnd, WM_APP + 0x51, (WPARAM)w, (LPARAM)h);
            return;
        }
        if (!editorWnd) return;
        inResize = true;
        RECT r{0, 0, w, h};
        DWORD style = (DWORD)GetWindowLongPtrW(editorWnd, GWL_STYLE);
        AdjustWindowRectEx(&r, style, FALSE, 0);
        SetWindowPos(editorWnd, nullptr, 0, 0, r.right - r.left, r.bottom - r.top, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        inResize = false;
    }
};

static void FreeInstance(Instance* in);
static void DetachGuard(Instance* in);

tresult PLUGIN_API ComponentHandler::performEdit(ParamID id, ParamValue v)
{
    if (inst) inst->queue.Push(id, v);
    return kResultOk;
}
tresult PLUGIN_API ComponentHandler::restartComponent(int32 flags)
{
    if (!inst) return kResultOk;
    if (flags & kLatencyChanged) inst->pending |= kPendLatency;
    if (flags & (kParamValuesChanged | kParamTitlesChanged)) inst->pending |= kPendParams;
    if (flags & (kReloadComponent | kIoChanged)) inst->pending |= kPendRestart;
    return kResultOk;
}
tresult PLUGIN_API PlugFrame::resizeView(IPlugView* v, ViewRect* r)
{
    if (!inst || !r) return kInvalidArgument;
    inst->ResizeEditor(r->getWidth(), r->getHeight());
    if (v) v->onSize(r);
    return kResultTrue;
}

// ============================================================================ callback do host VST2
static thread_local Instance* tLoading = nullptr;
static intptr_t __cdecl V2Host(AEffect* e, int32_t op, int32_t index, intptr_t value, void* ptr, float)
{
    Instance* in = e && e->user ? (Instance*)e->user : tLoading;
    switch (op)
    {
    case 1: return 2400;                                     // audioMasterVersion
    case 6: return 1;                                        // wantMidi (aceita, ignora)
    case 7:                                                  // getTime
        if (!in) return 0;
        in->timeInfo.sampleRate = in->sr;
        return (intptr_t)&in->timeInfo;
    case 13: if (in) in->pending |= kPendLatency; return 1;  // ioChanged (latencia pode ter mudado)
    case 15: if (in) in->ResizeEditor(index, (int)value); return 1; // sizeWindow
    case 16: return in ? (intptr_t)in->sr : 48000;           // getSampleRate
    case 17: return in ? in->maxBlock : 480;                 // getBlockSize
    case 23: return 0;                                       // getCurrentProcessLevel (desconhecido)
    case 32: if (ptr) strcpy_s((char*)ptr, 64, "TakaVox"); return 1; // vendor
    case 33: if (ptr) strcpy_s((char*)ptr, 64, "TakaVox"); return 1; // product
    case 34: return 1000;                                    // vendor version
    case 37:                                                 // canDo
        if (!ptr) return 0;
        if (strcmp((const char*)ptr, "sizeWindow") == 0) return 1;
        if (strcmp((const char*)ptr, "sendVstTimeInfo") == 0) return 1;
        return 0;
    case 42: return 1;                                       // updateDisplay
    case 43: case 44: return 1;                              // begin/endEdit
    default: return 0;
    }
}

// ============================================================================ API: iniciar e escanear
struct TvhInfo
{
    wchar_t name[128];
    wchar_t vendor[128];
    wchar_t category[128];
    int format;
    int classIndex;
    int isInstrument;
};

TVH_API int tvh_init()
{
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED); // a thread da interface do WPF ja e STA; aqui so garante
    EnsureHost();
    return 1;
}

static int ScanImpl(const wchar_t* path, TvhInfo* out, int maxOut, std::string& err)
{
    EnsureHost();
    bool vst3 = IsVst3Path(path);
    if (!CheckArch(path, vst3, err)) return -1;
    int count = 0;
    if (vst3)
    {
        auto mod = GetVst3Module(path, err);
        if (!mod) { if (err.empty()) err = "não é um VST3 válido"; return -1; }
        auto factory = mod->getFactory();
        std::string fvendor = factory.info().vendor();
        int idx = 0;
        for (auto& ci : factory.classInfos())
        {
            if (ci.category() != kVstAudioEffectClass) continue;
            if (count < maxOut)
            {
                TvhInfo& t = out[count];
                memset(&t, 0, sizeof(t));
                CopyW(t.name, 128, ci.name());
                CopyW(t.vendor, 128, ci.vendor().empty() ? fvendor : ci.vendor());
                std::string sub = ci.subCategoriesString();
                CopyW(t.category, 128, sub);
                t.format = 3;
                t.classIndex = idx;
                t.isInstrument = (sub.find("Instrument") != std::string::npos && sub.find("Fx") == std::string::npos) ? 1 : 0;
            }
            count++;
            idx++;
        }
        if (count == 0) err = "o arquivo não tem nenhum efeito de áudio";
        return count;
    }
    // VST2
    HMODULE h = GetVst2Module(path);
    if (!h) { err = "não deu para carregar a DLL (faltando arquivos do plugin?)"; return -1; }
    auto entry = (Vst2Entry)GetProcAddress(h, "VSTPluginMain");
    if (!entry) entry = (Vst2Entry)GetProcAddress(h, "main");
    if (!entry) { err = "não é um plugin VST"; return -1; }
    Instance* tmp = new Instance();
    tLoading = tmp;
    AEffect* e = entry(V2Host);
    tLoading = nullptr;
    if (!e || e->magic != kVstMagic) { delete tmp; err = "não é um plugin VST2 válido"; return -1; }
    e->user = tmp;
    e->dispatcher(e, effOpen, 0, 0, nullptr, 0);
    char nm[256] = {0}, vd[256] = {0};
    e->dispatcher(e, effGetEffectName, 0, 0, nm, 0);
    if (!nm[0]) e->dispatcher(e, effGetProductString, 0, 0, nm, 0);
    e->dispatcher(e, effGetVendorString, 0, 0, vd, 0);
    if (maxOut > 0)
    {
        TvhInfo& t = out[0];
        memset(&t, 0, sizeof(t));
        std::wstring fn = path;
        size_t sl = fn.find_last_of(L"\\/");
        std::wstring base = fn.substr(sl == std::wstring::npos ? 0 : sl + 1);
        if (base.size() > 4) base = base.substr(0, base.size() - 4);
        CopyW(t.name, 128, nm[0] ? FromAnsi(nm) : base);
        CopyW(t.vendor, 128, FromAnsi(vd));
        bool synth = (e->flags & effFlagsIsSynth) || e->numInputs == 0;
        CopyW(t.category, 128, synth ? std::wstring(L"Instrument") : std::wstring(L"Fx"));
        t.format = 2;
        t.classIndex = 0;
        t.isInstrument = synth ? 1 : 0;
    }
    e->dispatcher(e, effClose, 0, 0, nullptr, 0);
    delete tmp;
    return 1;
}

static int ScanGuard(const wchar_t* path, TvhInfo* out, int maxOut, std::string* err)
{
    __try { return ScanImpl(path, out, maxOut, *err); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -2; }
}

/// Lista os efeitos de um arquivo de plugin. Retorna quantos achou, -1 com erro em err, -2 se o plugin travou.
TVH_API int tvh_scan(const wchar_t* path, TvhInfo* out, int maxOut, wchar_t* err, int errCap)
{
    std::string e;
    int r = ScanGuard(path, out, maxOut, &e);
    if (r == -2) e = "o plugin travou ao ser lido";
    if (r < 0) CopyW(err, errCap, e);
    return r;
}

// ============================================================================ carregar
static bool SetupV3(Instance* in, const wchar_t* path, int classIndex, std::string& err)
{
    in->module = GetVst3Module(path, err);
    if (!in->module) { if (err.empty()) err = "não é um VST3 válido"; return false; }
    auto factory = in->module->getFactory();
    int idx = 0;
    VST3::Hosting::ClassInfo chosen;
    bool found = false;
    for (auto& ci : factory.classInfos())
    {
        if (ci.category() != kVstAudioEffectClass) continue;
        if (idx++ == classIndex) { chosen = ci; found = true; break; }
    }
    if (!found) { err = "efeito não encontrado no arquivo"; return false; }
    in->name = FromUtf8(chosen.name());
    in->provider = owned(new PlugProvider(factory, chosen, true));
    if (!in->provider->initialize()) { err = "o plugin não inicializou"; return false; }
    in->component = in->provider->getComponentPtr();
    in->controller = in->provider->getControllerPtr();
    if (!in->component) { err = "o plugin não criou o componente de áudio"; return false; }
    IComponent* comp = in->component;
    in->processor = FUnknownPtr<IAudioProcessor>(comp);
    if (!in->processor) { err = "o plugin não processa áudio"; return false; }
    if (in->processor->canProcessSampleSize(kSample32) != kResultTrue) { err = "o plugin não aceita áudio de 32 bits"; return false; }

    // canais: tenta mono->mono, depois estereo->estereo, depois mono->estereo
    int32 nIn = in->component->getBusCount(kAudio, kInput), nOut = in->component->getBusCount(kAudio, kOutput);
    if (nIn < 1 || nOut < 1) { err = "não é um efeito (sem entrada ou saída de áudio)"; return false; }
    std::vector<SpeakerArrangement> ia(nIn), oa(nOut);
    for (int32 i = 0; i < nIn; i++) in->processor->getBusArrangement(kInput, i, ia[i]);
    for (int32 i = 0; i < nOut; i++) in->processor->getBusArrangement(kOutput, i, oa[i]);
    const SpeakerArrangement tries[3][2] = {{SpeakerArr::kMono, SpeakerArr::kMono}, {SpeakerArr::kStereo, SpeakerArr::kStereo}, {SpeakerArr::kMono, SpeakerArr::kStereo}};
    for (auto& t : tries)
    {
        ia[0] = t[0];
        oa[0] = t[1];
        if (in->processor->setBusArrangements(ia.data(), nIn, oa.data(), nOut) == kResultTrue) break;
    }
    SpeakerArrangement a0 = 0, b0 = 0;
    in->processor->getBusArrangement(kInput, 0, a0);
    in->processor->getBusArrangement(kOutput, 0, b0);
    in->inCh = SpeakerArr::getChannelCount(a0);
    in->outCh = SpeakerArr::getChannelCount(b0);
    if (in->inCh < 1 || in->outCh < 1) { err = "configuração de canais não suportada"; return false; }
    in->component->activateBus(kAudio, kInput, 0, true);
    in->component->activateBus(kAudio, kOutput, 0, true);

    ProcessSetup ps{kRealtime, kSample32, in->maxBlock, in->sr};
    if (in->processor->setupProcessing(ps) != kResultOk) { err = "o plugin recusou 48 kHz com blocos de 10 ms"; return false; }

    in->handler.inst = in;
    in->frame.inst = in;
    int32 nParams = 16;
    if (in->controller)
    {
        in->controller->setComponentHandler(&in->handler);
        nParams = std::max<int32>(16, in->controller->getParameterCount());
        // a janela do plugin comeca mostrando o estado real do processador
        MemoryStream st;
        if (in->component->getState(&st) == kResultOk)
        {
            st.seek(0, IBStream::kIBSeekSet, nullptr);
            in->controller->setComponentState(&st);
        }
    }
    in->changes.setMaxParameters(nParams);
    in->outChanges.setMaxParameters(nParams);
    if (!in->data.prepare(*in->component, in->maxBlock, kSample32)) { err = "falha ao preparar os buffers de áudio"; return false; }
    in->ctx.sampleRate = in->sr;
    in->ctx.tempo = 120;
    in->ctx.timeSigNumerator = 4;
    in->ctx.timeSigDenominator = 4;
    in->ctx.state = ProcessContext::kTempoValid | ProcessContext::kTimeSigValid | ProcessContext::kContTimeValid;
    return true;
}

static bool ActivateV3(Instance* in, std::string& err)
{
    if (in->component->setActive(true) != kResultOk) { err = "o plugin não ativou"; return false; }
    in->processor->setProcessing(true);
    in->latency = (int)in->processor->getLatencySamples();
    return true;
}

static bool SetupV2(Instance* in, const wchar_t* path, std::string& err)
{
    in->dll = GetVst2Module(path);
    if (!in->dll) { err = "não deu para carregar a DLL (faltando arquivos do plugin?)"; return false; }
    auto entry = (Vst2Entry)GetProcAddress(in->dll, "VSTPluginMain");
    if (!entry) entry = (Vst2Entry)GetProcAddress(in->dll, "main");
    if (!entry) { err = "não é um plugin VST"; return false; }
    in->timeInfo.sampleRate = in->sr;
    in->timeInfo.tempo = 120;
    in->timeInfo.timeSigNumerator = 4;
    in->timeInfo.timeSigDenominator = 4;
    in->timeInfo.flags = (1 << 9) | (1 << 10) | (1 << 13); // ppqPos, tempo e compasso validos
    tLoading = in;
    in->eff = entry(V2Host);
    tLoading = nullptr;
    if (!in->eff || in->eff->magic != kVstMagic) { in->eff = nullptr; err = "não é um plugin VST2 válido"; return false; }
    in->eff->user = in;
    AEffect* e = in->eff;
    e->dispatcher(e, effOpen, 0, 0, nullptr, 0);
    e->dispatcher(e, effSetSampleRate, 0, 0, nullptr, (float)in->sr);
    e->dispatcher(e, effSetBlockSize, 0, in->maxBlock, nullptr, 0);
    if (e->numInputs < 1 || e->numOutputs < 1) { err = "não é um efeito (sem entrada ou saída de áudio)"; return false; }
    if (!(e->flags & effFlagsCanReplacing) || !e->processReplacing) { err = "plugin VST2 antigo demais (sem processReplacing)"; return false; }
    char nm[256] = {0};
    e->dispatcher(e, effGetEffectName, 0, 0, nm, 0);
    if (!nm[0]) e->dispatcher(e, effGetProductString, 0, 0, nm, 0);
    in->name = FromAnsi(nm);
    in->v2in.assign(e->numInputs, std::vector<float>(in->maxBlock));
    in->v2out.assign(e->numOutputs, std::vector<float>(in->maxBlock));
    for (auto& v : in->v2in) in->v2inPtr.push_back(v.data());
    for (auto& v : in->v2out) in->v2outPtr.push_back(v.data());
    in->inCh = e->numInputs;
    in->outCh = e->numOutputs;
    return true;
}

static void ActivateV2(Instance* in)
{
    AEffect* e = in->eff;
    e->dispatcher(e, effMainsChanged, 0, 1, nullptr, 0);
    e->dispatcher(e, effStartProcess, 0, 0, nullptr, 0);
    in->latency = e->initialDelay;
}

static bool SetStateInternal(Instance* in, const unsigned char* buf, int len);

static Instance* LoadImpl(const wchar_t* path, int classIndex, double sampleRate, int maxBlock,
                          const unsigned char* state, int stateLen, std::string& e)
{
    EnsureHost();
    bool vst3 = IsVst3Path(path);
    if (!CheckArch(path, vst3, e)) return nullptr;
    auto* in = new Instance();
    in->sr = sampleRate;
    in->maxBlock = maxBlock;
    bool ok;
    if (vst3)
    {
        in->format = 3;
        ok = SetupV3(in, path, classIndex, e);
        if (ok && state && stateLen > 0) SetStateInternal(in, state, stateLen);
        if (ok) ok = ActivateV3(in, e);
    }
    else
    {
        in->format = 2;
        ok = SetupV2(in, path, e);
        if (ok && state && stateLen > 0) SetStateInternal(in, state, stateLen);
        if (ok) ActivateV2(in);
    }
    if (!ok)
    {
        FreeInstance(in);
        return nullptr;
    }
    return in;
}

static Instance* LoadGuard(const wchar_t* path, int classIndex, double sampleRate, int maxBlock,
                           const unsigned char* state, int stateLen, std::string* e, bool* crashed)
{
    __try { return LoadImpl(path, classIndex, sampleRate, maxBlock, state, stateLen, *e); }
    __except (EXCEPTION_EXECUTE_HANDLER) { *crashed = true; return nullptr; }
}

/// Carrega e ativa um efeito. state/stateLen opcionais (preset salvo). Retorna null com o erro em err.
TVH_API void* tvh_load(const wchar_t* path, int classIndex, double sampleRate, int maxBlock,
                       const unsigned char* state, int stateLen, wchar_t* err, int errCap)
{
    std::string e;
    bool crashed = false;
    Instance* in = LoadGuard(path, classIndex, sampleRate, maxBlock, state, stateLen, &e, &crashed);
    if (crashed) e = "o plugin travou ao abrir";
    if (!in) CopyW(err, errCap, e.empty() ? std::string("erro desconhecido") : e);
    return in;
}

TVH_API int tvh_get_name(void* h, wchar_t* out, int cap)
{
    if (!h) return 0;
    CopyW(out, cap, ((Instance*)h)->name);
    return 1;
}

TVH_API int tvh_latency(void* h) { return h ? ((Instance*)h)->latency : 0; }
TVH_API int tvh_failed(void* h) { return h && ((Instance*)h)->failed ? 1 : 0; }
TVH_API int tvh_channels(void* h) { return h ? ((Instance*)h)->inCh * 100 + ((Instance*)h)->outCh : 0; }

/// Avisos pendentes do plugin (1 = latencia mudou, 2 = parametros mudaram, 4 = pediu reinicio). Zera ao ler.
TVH_API int tvh_poll(void* h) { return h ? ((Instance*)h)->pending.exchange(0) : 0; }

// ============================================================================ processamento (thread de audio)
static int SafeProcessV3(IAudioProcessor* p, ProcessData* d)
{
    __try { return p->process(*d) == kResultOk ? 0 : 1; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -999; }
}
static int SafeProcessV2(AEffect* e, float** in, float** out, int n)
{
    __try { e->processReplacing(e, in, out, n); return 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -999; }
}

/// Processa n amostras mono no lugar. Retorna 0 ok, -2 se o plugin travou (fica desligado de vez).
TVH_API int tvh_process(void* h, float* io, int n)
{
    auto* in = (Instance*)h;
    if (!in || in->failed || n <= 0 || n > in->maxBlock) return -1;
    unsigned int csr = _mm_getcsr();
    _mm_setcsr(csr | 0x8040); // denormais viram zero (evita picos de CPU em reverbs e filtros)
    int result = -1;
    if (in->format == 3)
    {
        ProcessData& d = in->data;
        for (int32 b = 0; b < d.numInputs; b++)
        {
            d.inputs[b].silenceFlags = 0;
            for (int32 c = 0; c < d.inputs[b].numChannels; c++)
            {
                float* ch = d.inputs[b].channelBuffers32[c];
                if (!ch) continue;
                if (b == 0) memcpy(ch, io, n * sizeof(float));
                else memset(ch, 0, n * sizeof(float));
            }
        }
        for (int32 b = 0; b < d.numOutputs; b++) d.outputs[b].silenceFlags = 0;
        in->changes.clearQueue();
        ParamChange pc;
        while (in->queue.Pop(pc))
        {
            int32 qi = 0, pi = 0;
            if (auto* q = in->changes.addParameterData(pc.id, qi)) q->addPoint(0, pc.value, pi);
        }
        in->outChanges.clearQueue();
        in->evIn.clear();
        in->evOut.clear();
        d.numSamples = n;
        d.inputParameterChanges = &in->changes;
        d.outputParameterChanges = &in->outChanges;
        d.processContext = &in->ctx;
        d.inputEvents = &in->evIn;
        d.outputEvents = &in->evOut;
        int r = SafeProcessV3(in->processor, &d);
        in->ctx.projectTimeSamples += n;
        in->ctx.continousTimeSamples += n;
        if (r == -999) { in->failed = true; result = -2; }
        else
        {
            // o que o plugin publicou (medidores etc.) volta para a janela dele
            int32 np = in->outChanges.getParameterCount();
            for (int32 i = 0; i < np; i++)
            {
                IParamValueQueue* q = in->outChanges.getParameterData(i);
                int32 cnt = q ? q->getPointCount() : 0;
                int32 off = 0;
                ParamValue v = 0;
                if (cnt > 0 && q->getPoint(cnt - 1, off, v) == kResultTrue) in->outQueue.Push(q->getParameterId(), v);
            }
            AudioBusBuffers& o = d.outputs[0];
            int oc = o.numChannels;
            float* c0 = oc > 0 && !(o.silenceFlags & 1) ? o.channelBuffers32[0] : nullptr;
            float* c1 = oc > 1 && !(o.silenceFlags & 2) ? o.channelBuffers32[1] : nullptr;
            if (oc <= 1) { if (c0) memcpy(io, c0, n * sizeof(float)); else memset(io, 0, n * sizeof(float)); }
            else
                for (int i = 0; i < n; i++) io[i] = 0.5f * ((c0 ? c0[i] : 0.f) + (c1 ? c1[i] : 0.f));
            result = 0;
        }
    }
    else if (in->format == 2)
    {
        in->timeInfo.ppqPos = in->timeInfo.samplePos / in->sr * in->timeInfo.tempo / 60.0;
        for (auto& v : in->v2in) memcpy(v.data(), io, n * sizeof(float));
        for (auto& v : in->v2out) memset(v.data(), 0, n * sizeof(float));
        int r = SafeProcessV2(in->eff, in->v2inPtr.data(), in->v2outPtr.data(), n);
        in->timeInfo.samplePos += n;
        if (r == -999) { in->failed = true; result = -2; }
        else
        {
            if (in->outCh == 1) memcpy(io, in->v2out[0].data(), n * sizeof(float));
            else for (int i = 0; i < n; i++) io[i] = 0.5f * (in->v2out[0][i] + in->v2out[1][i]);
            result = 0;
        }
    }
    _mm_setcsr(csr);
    return result;
}

/// Reinicia o processamento (aplica nova latencia). So chamar com o plugin FORA da cadeia de audio.
TVH_API int tvh_reactivate(void* h)
{
    auto* in = (Instance*)h;
    if (!in || in->failed) return 0;
    if (in->format == 3)
    {
        in->processor->setProcessing(false);
        in->component->setActive(false);
        in->component->setActive(true);
        in->processor->setProcessing(true);
        in->latency = (int)in->processor->getLatencySamples();
    }
    else if (in->format == 2)
    {
        AEffect* e = in->eff;
        e->dispatcher(e, effStopProcess, 0, 0, nullptr, 0);
        e->dispatcher(e, effMainsChanged, 0, 0, nullptr, 0);
        e->dispatcher(e, effMainsChanged, 0, 1, nullptr, 0);
        e->dispatcher(e, effStartProcess, 0, 0, nullptr, 0);
        in->latency = e->initialDelay;
    }
    return 1;
}

/// Chamar pela interface ~30x/s: entrega a janela do plugin o que o audio publicou.
TVH_API void tvh_idle(void* h)
{
    auto* in = (Instance*)h;
    if (!in || in->format != 3 || !in->controller) return;
    ParamChange pc;
    while (in->outQueue.Pop(pc)) in->controller->setParamNormalized(pc.id, pc.value);
}

// ============================================================================ parametros (thread da interface)
TVH_API int tvh_param_count(void* h)
{
    auto* in = (Instance*)h;
    if (!in) return 0;
    if (in->format == 3) return in->controller ? in->controller->getParameterCount() : 0;
    return in->eff ? in->eff->numParams : 0;
}

/// Nome, texto exibido, valor (0..1), passos (0 = continuo) e valor padrao do parametro i.
/// Retorna 0 se o parametro deve ficar escondido, 1 normal, 2 se e o bypass do plugin.
TVH_API int tvh_param_info(void* h, int i, wchar_t* name, int ncap, wchar_t* display, int dcap,
                           double* value, int* steps, double* def)
{
    auto* in = (Instance*)h;
    if (!in) return 0;
    if (in->format == 3 && in->controller)
    {
        ParameterInfo pi{};
        if (in->controller->getParameterInfo(i, pi) != kResultOk) return 0;
        if (pi.flags & (ParameterInfo::kIsReadOnly | ParameterInfo::kIsHidden)) return 0;
        CopyW(name, ncap, FromString128(pi.title));
        double v = in->controller->getParamNormalized(pi.id);
        if (value) *value = v;
        if (steps) *steps = pi.stepCount;
        if (def) *def = pi.defaultNormalizedValue;
        String128 s{};
        in->controller->getParamStringByValue(pi.id, v, s);
        std::wstring disp = FromString128(s), unit = FromString128(pi.units);
        if (!unit.empty()) disp += L" " + unit;
        CopyW(display, dcap, disp);
        return (pi.flags & ParameterInfo::kIsBypass) ? 2 : 1;
    }
    if (in->format == 2 && in->eff)
    {
        char nm[256] = {0}, disp[256] = {0}, lab[256] = {0};
        in->eff->dispatcher(in->eff, effGetParamName, i, 0, nm, 0);
        in->eff->dispatcher(in->eff, effGetParamDisplay, i, 0, disp, 0);
        in->eff->dispatcher(in->eff, effGetParamLabel, i, 0, lab, 0);
        CopyW(name, ncap, FromAnsi(nm));
        std::wstring d = FromAnsi(disp);
        std::wstring l = FromAnsi(lab);
        if (!l.empty()) d += L" " + l;
        CopyW(display, dcap, d);
        if (value) *value = in->eff->getParameter(in->eff, i);
        if (steps) *steps = 0;
        if (def) *def = -1;
        return 1;
    }
    return 0;
}

TVH_API void tvh_param_set(void* h, int i, double v)
{
    auto* in = (Instance*)h;
    if (!in) return;
    if (v < 0) v = 0;
    if (v > 1) v = 1;
    if (in->format == 3 && in->controller)
    {
        ParameterInfo pi{};
        if (in->controller->getParameterInfo(i, pi) != kResultOk) return;
        in->controller->setParamNormalized(pi.id, v);
        in->queue.Push(pi.id, v);
    }
    else if (in->format == 2 && in->eff) in->eff->setParameter(in->eff, i, (float)v);
}

// ---------------------------------------------------------------------------- parametros pelo ID (VST3)
// Usados pela voz com IA, que conhece os parametros do plugin pelo ID e trabalha com valores reais (semitons, dB).

/// ID do parametro i (VST3: ParamID; VST2: o proprio indice). -1 se nao existir.
TVH_API int tvh_param_id(void* h, int i)
{
    auto* in = (Instance*)h;
    if (!in || i < 0) return -1;
    if (in->format == 3 && in->controller)
    {
        ParameterInfo pi{};
        if (in->controller->getParameterInfo(i, pi) != kResultOk) return -1;
        return (int)pi.id;
    }
    return in->format == 2 && in->eff && i < in->eff->numParams ? i : -1;
}

/// Valor real (ex.: semitons) -> normalizado 0..1, pela escala do proprio plugin (so VST3).
TVH_API double tvh_param_to_norm(void* h, int id, double plain)
{
    auto* in = (Instance*)h;
    if (!in || in->format != 3 || !in->controller) return plain;
    return in->controller->plainParamToNormalized((ParamID)id, plain);
}

/// Normalizado 0..1 -> valor real, pela escala do proprio plugin (so VST3).
TVH_API double tvh_param_to_plain(void* h, int id, double norm)
{
    auto* in = (Instance*)h;
    if (!in || in->format != 3 || !in->controller) return norm;
    return in->controller->normalizedParamToPlain((ParamID)id, norm);
}

/// Valor normalizado atual do parametro (pelo ID). -1 se nao der.
TVH_API double tvh_param_get_id(void* h, int id)
{
    auto* in = (Instance*)h;
    if (!in || in->format != 3 || !in->controller) return -1;
    return in->controller->getParamNormalized((ParamID)id);
}

/// Muda o parametro pelo ID (normalizado 0..1): vale para a janela do plugin e para o audio (proximo bloco).
TVH_API void tvh_param_set_id(void* h, int id, double v)
{
    auto* in = (Instance*)h;
    if (!in || in->format != 3 || !in->controller) return;
    if (v < 0) v = 0;
    if (v > 1) v = 1;
    in->controller->setParamNormalized((ParamID)id, v);
    in->queue.Push((ParamID)id, v);
}

/// Texto que o plugin mostra para um valor (normalizado) do parametro.
TVH_API int tvh_param_text(void* h, int id, double norm, wchar_t* out, int cap)
{
    auto* in = (Instance*)h;
    if (!in || in->format != 3 || !in->controller) return 0;
    String128 s{};
    if (in->controller->getParamStringByValue((ParamID)id, norm, s) != kResultOk) return 0;
    CopyW(out, cap, FromString128(s));
    return 1;
}

// ============================================================================ estado (preset do plugin)
// formato: "TVS1" + formato(1) + [VST3: len(4) componente + len(4) controlador] [VST2: tipo(1) + len(4) dados]
static void PutU32(std::vector<unsigned char>& v, uint32_t x) { for (int i = 0; i < 4; i++) v.push_back((unsigned char)(x >> (8 * i))); }
static uint32_t GetU32(const unsigned char* p) { return p[0] | (p[1] << 8) | (p[2] << 16) | ((uint32_t)p[3] << 24); }

static std::vector<unsigned char> GetStateInternal(Instance* in)
{
    std::vector<unsigned char> out = {'T', 'V', 'S', '1', (unsigned char)in->format};
    if (in->format == 3)
    {
        MemoryStream a, b;
        in->component->getState(&a);
        if (in->controller) in->controller->getState(&b);
        PutU32(out, (uint32_t)a.getSize());
        if (a.getSize() > 0) out.insert(out.end(), (unsigned char*)a.getData(), (unsigned char*)a.getData() + a.getSize());
        PutU32(out, (uint32_t)b.getSize());
        if (b.getSize() > 0) out.insert(out.end(), (unsigned char*)b.getData(), (unsigned char*)b.getData() + b.getSize());
    }
    else if (in->format == 2)
    {
        AEffect* e = in->eff;
        if (e->flags & effFlagsProgramChunks)
        {
            void* p = nullptr;
            intptr_t n = e->dispatcher(e, effGetChunk, 0, 0, &p, 0);
            out.push_back(0);
            PutU32(out, (uint32_t)(p && n > 0 ? n : 0));
            if (p && n > 0) out.insert(out.end(), (unsigned char*)p, (unsigned char*)p + n);
        }
        else
        {
            out.push_back(1);
            PutU32(out, (uint32_t)(e->numParams * 4));
            for (int i = 0; i < e->numParams; i++)
            {
                float f = e->getParameter(e, i);
                unsigned char* b = (unsigned char*)&f;
                out.insert(out.end(), b, b + 4);
            }
        }
    }
    return out;
}

static bool SetStateInternal(Instance* in, const unsigned char* buf, int len)
{
    if (!buf || len < 9 || memcmp(buf, "TVS1", 4) != 0 || buf[4] != in->format) return false;
    const unsigned char* p = buf + 5;
    const unsigned char* end = buf + len;
    if (in->format == 3)
    {
        uint32_t la = GetU32(p);
        p += 4;
        if (p + la > end) return false;
        const unsigned char* compData = p;
        p += la;
        if (la > 0)
        {
            MemoryStream a((void*)compData, la);
            in->component->setState(&a);
            if (in->controller)
            {
                MemoryStream a2((void*)compData, la);
                in->controller->setComponentState(&a2);
            }
        }
        if (in->controller && p + 4 <= end)
        {
            uint32_t lb = GetU32(p);
            p += 4;
            if (lb > 0 && p + lb <= end)
            {
                MemoryStream b((void*)p, lb);
                in->controller->setState(&b);
            }
        }
        in->pending |= kPendParams;
        return true;
    }
    if (in->format == 2)
    {
        AEffect* e = in->eff;
        unsigned char type = *p++;
        if (p + 4 > end) return false;
        uint32_t n = GetU32(p);
        p += 4;
        if (p + n > end) return false;
        if (type == 0 && n > 0) e->dispatcher(e, effSetChunk, 0, n, (void*)p, 0);
        else if (type == 1)
            for (int i = 0; i < e->numParams && (uint32_t)(i * 4 + 4) <= n; i++)
            {
                float f;
                memcpy(&f, p + i * 4, 4);
                e->setParameter(e, i, f);
            }
        in->pending |= kPendParams;
        return true;
    }
    return false;
}

/// Copia o estado em buf. Retorna o tamanho total (se cap for menor, so informa o tamanho).
TVH_API int tvh_get_state(void* h, unsigned char* buf, int cap)
{
    auto* in = (Instance*)h;
    if (!in || in->failed) return 0;
    auto s = GetStateInternal(in);
    if (buf && cap >= (int)s.size()) memcpy(buf, s.data(), s.size());
    return (int)s.size();
}

TVH_API int tvh_set_state(void* h, const unsigned char* buf, int len)
{
    auto* in = (Instance*)h;
    return in && !in->failed && SetStateInternal(in, buf, len) ? 1 : 0;
}

// ============================================================================ janela original do plugin
static const wchar_t* kWndClass = L"TakaVoxPluginWindow";

static LRESULT CALLBACK EditorProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    auto* in = (Instance*)GetWindowLongPtrW(hwnd, GWLP_USERDATA);
    switch (msg)
    {
    case WM_CLOSE:
        DestroyWindow(hwnd);
        return 0;
    case WM_DESTROY:
        if (in)
        {
            KillTimer(hwnd, 1);
            if (in->format == 3 && in->view)
            {
                in->view->setFrame(nullptr);
                in->view->removed();
                in->view = nullptr;
            }
            else if (in->format == 2 && in->eff) in->eff->dispatcher(in->eff, effEditClose, 0, 0, nullptr, 0);
            in->editorWnd = nullptr;
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
        }
        return 0;
    case WM_SIZING:
        if (in && in->format == 3 && in->view && in->resizable)
        {
            RECT* wr = (RECT*)lp;
            RECT fr{0, 0, 0, 0};
            AdjustWindowRectEx(&fr, (DWORD)GetWindowLongPtrW(hwnd, GWL_STYLE), FALSE, 0);
            int bw = fr.right - fr.left, bh = fr.bottom - fr.top;
            ViewRect r(0, 0, (wr->right - wr->left) - bw, (wr->bottom - wr->top) - bh);
            if (in->view->checkSizeConstraint(&r) == kResultTrue)
            {
                int nw = r.getWidth() + bw, nh = r.getHeight() + bh;
                if (wp == WMSZ_LEFT || wp == WMSZ_TOPLEFT || wp == WMSZ_BOTTOMLEFT) wr->left = wr->right - nw; else wr->right = wr->left + nw;
                if (wp == WMSZ_TOP || wp == WMSZ_TOPLEFT || wp == WMSZ_TOPRIGHT) wr->top = wr->bottom - nh; else wr->bottom = wr->top + nh;
            }
            return TRUE;
        }
        break;
    case WM_SIZE:
        if (in && in->format == 3 && in->view && in->resizable && !in->inResize && wp != SIZE_MINIMIZED)
        {
            ViewRect r(0, 0, LOWORD(lp), HIWORD(lp));
            in->view->onSize(&r);
        }
        break;
    case WM_TIMER:
        if (in && in->format == 2 && in->eff) in->eff->dispatcher(in->eff, effEditIdle, 0, 0, nullptr, 0);
        return 0;
    case WM_ERASEBKGND:
        return 1;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

TVH_API int tvh_has_editor(void* h)
{
    auto* in = (Instance*)h;
    if (!in || in->failed) return 0;
    if (in->format == 2) return in->eff && (in->eff->flags & effFlagsHasEditor) ? 1 : 0;
    if (!in->controller) return 0;
    if (in->hasEditor < 0)
    {
        IPtr<IPlugView> v = owned(in->controller->createView(ViewType::kEditor));
        in->hasEditor = v && v->isPlatformTypeSupported(kPlatformTypeHWND) == kResultTrue ? 1 : 0;
    }
    return in->hasEditor;
}

static HWND OpenEditorImpl(Instance* in, HWND owner, const wchar_t* title)
{
    static bool registered = false;
    if (!registered)
    {
        WNDCLASSEXW wc{sizeof(wc)};
        wc.lpfnWndProc = EditorProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        wc.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);
        wc.lpszClassName = kWndClass;
        RegisterClassExW(&wc);
        registered = true;
    }
    // VST2 (quase sempre antigos): janela "system aware" para o Windows ampliar se a tela tiver zoom
    DPI_AWARENESS_CONTEXT oldCtx = nullptr;
    if (in->format == 2) oldCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_SYSTEM_AWARE);

    int w = 400, hgt = 300;
    if (in->format == 3)
    {
        in->view = owned(in->controller->createView(ViewType::kEditor));
        if (!in->view || in->view->isPlatformTypeSupported(kPlatformTypeHWND) != kResultTrue) { in->view = nullptr; return nullptr; }
        ViewRect r;
        if (in->view->getSize(&r) == kResultOk) { w = r.getWidth(); hgt = r.getHeight(); }
        in->resizable = in->view->canResize() == kResultTrue;
    }
    else
    {
        ERect* r = nullptr;
        in->eff->dispatcher(in->eff, effEditGetRect, 0, 0, &r, 0);
        if (r) { w = r->right - r->left; hgt = r->bottom - r->top; }
        in->resizable = false;
    }
    if (w < 50) w = 400;
    if (hgt < 50) hgt = 300;
    DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN;
    if (in->resizable) style |= WS_THICKFRAME | WS_MAXIMIZEBOX;
    RECT rc{0, 0, w, hgt};
    AdjustWindowRectEx(&rc, style, FALSE, 0);
    HWND wnd = CreateWindowExW(0, kWndClass, title && title[0] ? title : in->name.c_str(), style, CW_USEDEFAULT, CW_USEDEFAULT,
                               rc.right - rc.left, rc.bottom - rc.top, owner, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!wnd)
    {
        in->view = nullptr;
        if (oldCtx) SetThreadDpiAwarenessContext(oldCtx);
        return nullptr;
    }
    BOOL dark = TRUE;
    DwmSetWindowAttribute(wnd, 20, &dark, sizeof(dark)); // barra de titulo escura
    SetWindowLongPtrW(wnd, GWLP_USERDATA, (LONG_PTR)in);
    in->editorWnd = wnd;
    if (in->format == 3)
    {
        FUnknownPtr<IPlugViewContentScaleSupport> scale(in->view.get());
        if (scale)
        {
            UINT dpi = GetDpiForWindow(wnd);
            if (dpi > 0 && dpi != 96) scale->setContentScaleFactor((float)dpi / 96.0f);
        }
        in->view->setFrame(&in->frame);
        if (in->view->attached(wnd, kPlatformTypeHWND) != kResultOk)
        {
            in->view->setFrame(nullptr);
            in->view = nullptr;
            SetWindowLongPtrW(wnd, GWLP_USERDATA, 0);
            DestroyWindow(wnd);
            in->editorWnd = nullptr;
            return nullptr;
        }
        ViewRect r;
        if (in->view && in->view->getSize(&r) == kResultOk) in->ResizeEditor(r.getWidth(), r.getHeight());
    }
    else
    {
        in->eff->dispatcher(in->eff, effEditOpen, 0, 0, wnd, 0);
        ERect* r = nullptr;
        in->eff->dispatcher(in->eff, effEditGetRect, 0, 0, &r, 0);
        if (r && r->right - r->left > 0 && r->bottom - r->top > 0) in->ResizeEditor(r->right - r->left, r->bottom - r->top);
        SetTimer(wnd, 1, 30, nullptr);
        if (oldCtx) SetThreadDpiAwarenessContext(oldCtx);
    }
    ShowWindow(wnd, SW_SHOWNORMAL);
    return wnd;
}

static HWND OpenEditorGuard(Instance* in, HWND owner, const wchar_t* title, bool* crashed)
{
    __try { return OpenEditorImpl(in, owner, title); }
    __except (EXCEPTION_EXECUTE_HANDLER) { *crashed = true; return nullptr; }
}

/// Abre a janela original do plugin (ou traz para frente). Retorna o HWND ou null.
TVH_API void* tvh_open_editor(void* h, void* owner, const wchar_t* title)
{
    auto* in = (Instance*)h;
    if (!in || in->failed) return nullptr;
    if (in->editorWnd)
    {
        ShowWindow(in->editorWnd, IsIconic(in->editorWnd) ? SW_RESTORE : SW_SHOWNORMAL);
        SetForegroundWindow(in->editorWnd);
        return in->editorWnd;
    }
    if (in->format == 3 && !in->controller) return nullptr;
    if (in->format == 2 && (!in->eff || !(in->eff->flags & effFlagsHasEditor))) return nullptr;
    bool crashed = false;
    HWND w = OpenEditorGuard(in, (HWND)owner, title, &crashed);
    if (crashed)
    {
        in->failed = true; // nao confia mais nesse plugin
        if (in->editorWnd)
        {
            HWND ew = in->editorWnd;
            in->editorWnd = nullptr;
            SetWindowLongPtrW(ew, GWLP_USERDATA, 0);
            DestroyWindow(ew);
        }
        return nullptr;
    }
    return w;
}

TVH_API void tvh_close_editor(void* h)
{
    auto* in = (Instance*)h;
    if (!in) return;
    if (in->editorWnd) DestroyWindow(in->editorWnd);
    if (in->hostWnd) DetachGuard(in); // a moldura (WPF) fecha a janela filha depois
}

TVH_API int tvh_editor_open(void* h)
{
    auto* in = (Instance*)h;
    return in && (in->editorWnd || in->hostWnd) ? 1 : 0;
}

// ============================================================================ janela original EMBUTIDA na moldura do TakaVox
// A moldura (titulo, presets, botoes) e uma janela WPF; o plugin desenha dentro de uma janela filha criada aqui.
// Quando o plugin pede outro tamanho, a filha muda na hora e a moldura recebe WM_APP+0x51 (largura, altura).
static const wchar_t* kHostClass = L"TakaVoxPluginHost";

static void DetachHosted(Instance* in)
{
    if (!in || !in->hostWnd) return;
    KillTimer(in->hostWnd, 1);
    if (in->format == 3 && in->view)
    {
        in->view->setFrame(nullptr);
        in->view->removed();
        in->view = nullptr;
    }
    else if (in->format == 2 && in->eff) in->eff->dispatcher(in->eff, effEditClose, 0, 0, nullptr, 0);
    SetWindowLongPtrW(in->hostWnd, GWLP_USERDATA, 0);
    in->hostWnd = nullptr;
}

static void DetachGuard(Instance* in)
{
    __try { DetachHosted(in); }
    __except (EXCEPTION_EXECUTE_HANDLER) { in->failed = true; in->hostWnd = nullptr; in->view = nullptr; }
}

static LRESULT CALLBACK HostProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    auto* in = (Instance*)GetWindowLongPtrW(hwnd, GWLP_USERDATA);
    switch (msg)
    {
    case WM_SIZE:
        if (in && in->format == 3 && in->view && in->resizable && !in->inResize && wp != SIZE_MINIMIZED)
        {
            int w = LOWORD(lp), h = HIWORD(lp);
            ViewRect r(0, 0, w, h);
            if (in->view->checkSizeConstraint(&r) == kResultTrue && (r.getWidth() != w || r.getHeight() != h))
                PostMessageW(hwnd, WM_APP + 0x51, (WPARAM)r.getWidth(), (LPARAM)r.getHeight()); // a moldura ajusta
            in->view->onSize(&r);
        }
        break;
    case WM_TIMER:
        if (in && in->format == 2 && in->eff) in->eff->dispatcher(in->eff, effEditIdle, 0, 0, nullptr, 0);
        return 0;
    case WM_ERASEBKGND:
    {
        RECT rc;
        GetClientRect(hwnd, &rc);
        HBRUSH b = CreateSolidBrush(RGB(0x31, 0x37, 0x3B));
        FillRect((HDC)wp, &rc, b);
        DeleteObject(b);
        return 1;
    }
    case WM_DESTROY:
        if (in) DetachGuard(in);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

static HWND CreateHostImpl(Instance* in, HWND parent, int* outW, int* outH)
{
    static bool registered = false;
    if (!registered)
    {
        WNDCLASSEXW wc{sizeof(wc)};
        wc.lpfnWndProc = HostProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        wc.lpszClassName = kHostClass;
        RegisterClassExW(&wc);
        registered = true;
    }
    // VST2 (quase sempre antigos): "system aware" para o Windows ampliar se a tela tiver zoom (moldura em modo misto)
    DPI_AWARENESS_CONTEXT oldCtx = nullptr;
    if (in->format == 2) oldCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_SYSTEM_AWARE);

    int w = 400, hgt = 300;
    if (in->format == 3)
    {
        in->view = owned(in->controller->createView(ViewType::kEditor));
        if (!in->view || in->view->isPlatformTypeSupported(kPlatformTypeHWND) != kResultTrue)
        {
            in->view = nullptr;
            if (oldCtx) SetThreadDpiAwarenessContext(oldCtx);
            return nullptr;
        }
        ViewRect r;
        if (in->view->getSize(&r) == kResultOk) { w = r.getWidth(); hgt = r.getHeight(); }
        in->resizable = in->view->canResize() == kResultTrue;
    }
    else
    {
        ERect* r = nullptr;
        in->eff->dispatcher(in->eff, effEditGetRect, 0, 0, &r, 0);
        if (r) { w = r->right - r->left; hgt = r->bottom - r->top; }
        in->resizable = false;
    }
    if (w < 50) w = 400;
    if (hgt < 50) hgt = 300;
    HWND wnd = CreateWindowExW(0, kHostClass, L"", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                               0, 0, w, hgt, parent, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!wnd)
    {
        in->view = nullptr;
        if (oldCtx) SetThreadDpiAwarenessContext(oldCtx);
        return nullptr;
    }
    SetWindowLongPtrW(wnd, GWLP_USERDATA, (LONG_PTR)in);
    in->hostWnd = wnd;
    if (in->format == 3)
    {
        FUnknownPtr<IPlugViewContentScaleSupport> scale(in->view.get());
        if (scale)
        {
            UINT dpi = GetDpiForWindow(wnd);
            if (dpi > 0 && dpi != 96) scale->setContentScaleFactor((float)dpi / 96.0f);
        }
        in->view->setFrame(&in->frame);
        if (in->view->attached(wnd, kPlatformTypeHWND) != kResultOk)
        {
            in->view->setFrame(nullptr);
            in->view = nullptr;
            SetWindowLongPtrW(wnd, GWLP_USERDATA, 0);
            in->hostWnd = nullptr;
            DestroyWindow(wnd);
            return nullptr;
        }
        ViewRect r;
        if (in->view && in->view->getSize(&r) == kResultOk && r.getWidth() > 0 && r.getHeight() > 0) { w = r.getWidth(); hgt = r.getHeight(); }
    }
    else
    {
        in->eff->dispatcher(in->eff, effEditOpen, 0, 0, wnd, 0);
        ERect* r = nullptr;
        in->eff->dispatcher(in->eff, effEditGetRect, 0, 0, &r, 0);
        if (r && r->right - r->left > 0 && r->bottom - r->top > 0) { w = r->right - r->left; hgt = r->bottom - r->top; }
        SetTimer(wnd, 1, 30, nullptr);
        if (oldCtx) SetThreadDpiAwarenessContext(oldCtx);
    }
    in->inResize = true;
    SetWindowPos(wnd, nullptr, 0, 0, w, hgt, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    in->inResize = false;
    if (outW) *outW = w;
    if (outH) *outH = hgt;
    return wnd;
}

static HWND CreateHostGuard(Instance* in, HWND parent, int* w, int* h, bool* crashed)
{
    __try { return CreateHostImpl(in, parent, w, h); }
    __except (EXCEPTION_EXECUTE_HANDLER) { *crashed = true; return nullptr; }
}

/// Abre a janela original do plugin DENTRO de 'parent' (janela filha). Retorna o HWND da filha (tamanho em w/h) ou null.
TVH_API void* tvh_editor_create(void* h, void* parent, int* w, int* hgt)
{
    auto* in = (Instance*)h;
    if (!in || in->failed || !parent || in->hostWnd) return nullptr;
    if (in->format == 3 && !in->controller) return nullptr;
    if (in->format == 2 && (!in->eff || !(in->eff->flags & effFlagsHasEditor))) return nullptr;
    if (in->editorWnd) DestroyWindow(in->editorWnd); // um editor por vez
    bool crashed = false;
    HWND wnd = CreateHostGuard(in, (HWND)parent, w, hgt, &crashed);
    if (crashed)
    {
        in->failed = true; // nao confia mais nesse plugin
        if (in->hostWnd)
        {
            HWND hw = in->hostWnd;
            in->hostWnd = nullptr;
            in->view = nullptr;
            SetWindowLongPtrW(hw, GWLP_USERDATA, 0);
            DestroyWindow(hw);
        }
        return nullptr;
    }
    return wnd;
}

/// O plugin aceita mudar o tamanho da janela dele?
TVH_API int tvh_editor_resizable(void* h)
{
    auto* in = (Instance*)h;
    return in && (in->hostWnd || in->editorWnd) && in->resizable ? 1 : 0;
}

// ============================================================================ programas (presets de fabrica)
static void ProgInit(Instance* in)
{
    if (in->progKnown) return;
    in->progKnown = true;
    if (in->format == 2)
    {
        in->progCount = in->eff ? in->eff->numPrograms : 0;
        return;
    }
    if (!in->controller) return;
    int32 n = in->controller->getParameterCount();
    int32 steps = 0;
    for (int32 i = 0; i < n; i++)
    {
        ParameterInfo pi{};
        if (in->controller->getParameterInfo(i, pi) == kResultOk && (pi.flags & ParameterInfo::kIsProgramChange))
        {
            in->progParam = pi.id; in->progHasParam = true; steps = pi.stepCount;
            break;
        }
    }
    if (!in->progHasParam) return;
    FUnknownPtr<IUnitInfo> ui(in->controller);
    if (ui && ui->getProgramListCount() > 0)
    {
        ProgramListInfo li{};
        if (ui->getProgramListInfo(0, li) == kResultOk && li.programCount > 0) { in->progList = li.id; in->progCount = li.programCount; return; }
    }
    if (steps > 0) in->progCount = steps + 1;
}

static int ProgCountImpl(Instance* in) { ProgInit(in); return in->progCount > 1 ? in->progCount : 0; }

TVH_API int tvh_program_count(void* h)
{
    auto* in = (Instance*)h;
    if (!in || in->failed) return 0;
    __try { return ProgCountImpl(in); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

static int ProgNameImpl(Instance* in, int i, wchar_t* out, int cap)
{
    ProgInit(in);
    if (i < 0 || i >= in->progCount) return 0;
    std::wstring name;
    if (in->format == 2)
    {
        char buf[256] = {0};
        if (!in->eff->dispatcher(in->eff, effGetProgramNameIndexed, i, -1, buf, 0) || !buf[0])
        {
            if ((int)in->eff->dispatcher(in->eff, effGetProgram, 0, 0, nullptr, 0) == i) in->eff->dispatcher(in->eff, effGetProgramName, 0, 0, buf, 0);
        }
        name = FromAnsi(buf);
    }
    else
    {
        String128 s{};
        FUnknownPtr<IUnitInfo> ui(in->controller);
        if (in->progList != kNoProgramListId && ui && ui->getProgramName(in->progList, i, s) == kResultOk) name = FromString128(s);
        else if (in->progHasParam && in->progCount > 1 &&
                 in->controller->getParamStringByValue(in->progParam, (double)i / (in->progCount - 1), s) == kResultOk) name = FromString128(s);
    }
    if (name.empty()) name = L"Programa " + std::to_wstring(i + 1);
    CopyW(out, cap, name);
    return 1;
}

TVH_API int tvh_program_name(void* h, int i, wchar_t* out, int cap)
{
    auto* in = (Instance*)h;
    if (!in || in->failed || !out || cap <= 0) return 0;
    __try { return ProgNameImpl(in, i, out, cap); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

static int ProgGetImpl(Instance* in)
{
    ProgInit(in);
    if (in->progCount <= 1) return -1;
    if (in->format == 2) return (int)in->eff->dispatcher(in->eff, effGetProgram, 0, 0, nullptr, 0);
    double v = in->controller->getParamNormalized(in->progParam);
    int i = (int)(v * (in->progCount - 1) + 0.5);
    return i < 0 ? 0 : i >= in->progCount ? in->progCount - 1 : i;
}

TVH_API int tvh_get_program(void* h)
{
    auto* in = (Instance*)h;
    if (!in || in->failed) return -1;
    __try { return ProgGetImpl(in); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}

static int ProgSetImpl(Instance* in, int i)
{
    ProgInit(in);
    if (i < 0 || i >= in->progCount) return 0;
    if (in->format == 2)
    {
        in->eff->dispatcher(in->eff, effBeginSetProgram, 0, 0, nullptr, 0);
        in->eff->dispatcher(in->eff, effSetProgram, 0, i, nullptr, 0);
        in->eff->dispatcher(in->eff, effEndSetProgram, 0, 0, nullptr, 0);
        return 1;
    }
    double v = in->progCount > 1 ? (double)i / (in->progCount - 1) : 0;
    in->controller->setParamNormalized(in->progParam, v);
    in->queue.Push(in->progParam, v); // o processador tambem troca
    in->pending |= kPendParams;
    return 1;
}

/// Troca o programa (preset de fabrica) do plugin.
TVH_API int tvh_set_program(void* h, int i)
{
    auto* in = (Instance*)h;
    if (!in || in->failed) return 0;
    __try { return ProgSetImpl(in, i); }
    __except (EXCEPTION_EXECUTE_HANDLER) { in->failed = true; return 0; }
}

// ============================================================================ liberar
static void FreeInstance(Instance* in)
{
    if (!in) return;
    if (in->editorWnd) DestroyWindow(in->editorWnd);
    if (in->hostWnd) { HWND hw = in->hostWnd; DetachHosted(in); ShowWindow(hw, SW_HIDE); }
    if (in->format == 3)
    {
        if (!in->failed)
        {
            if (in->processor) in->processor->setProcessing(false);
            if (in->component) in->component->setActive(false);
        }
        if (in->controller) in->controller->setComponentHandler(nullptr);
        in->view = nullptr;
        in->processor = nullptr;
        in->controller = nullptr;
        in->component = nullptr;
        in->provider = nullptr; // termina e libera componente e controlador (desconecta os dois)
        in->module = nullptr;   // o modulo continua no cache
    }
    else if (in->format == 2)
    {
        if (in->eff)
        {
            if (!in->failed)
            {
                in->eff->dispatcher(in->eff, effStopProcess, 0, 0, nullptr, 0);
                in->eff->dispatcher(in->eff, effMainsChanged, 0, 0, nullptr, 0);
            }
            in->eff->dispatcher(in->eff, effClose, 0, 0, nullptr, 0);
            in->eff = nullptr;
        }
    }
    delete in;
}

static void FreeGuard(Instance* in)
{
    __try { FreeInstance(in); }
    __except (EXCEPTION_EXECUTE_HANDLER) { /* plugin travou ao fechar: vaza a memoria, mas o app segue */ }
}

/// Libera o plugin. So chamar com ele FORA da cadeia de audio.
TVH_API void tvh_free(void* h) { FreeGuard((Instance*)h); }
