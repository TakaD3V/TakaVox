// tvtest3.cpp - plugin VST3 minimo para testar o hospedeiro do TakaVox. NAO vai no instalador.
// Dois efeitos no mesmo arquivo: "TV Teste Ganho" (ganho 0..2x com atraso fixo de 64 amostras) e
// "TV Teste Inverte" (mesma coisa com a polaridade invertida). Processador e controlador separados,
// janela Win32 com slider (testa performEdit), pedido de redimensionamento (IPlugFrame) e um medidor
// que chega pela fila de parametros de saida. O parametro "Travar" provoca um acesso invalido de proposito.
#include "public.sdk/source/vst/vstaudioeffect.h"
#include "public.sdk/source/vst/vsteditcontroller.h"
#include "public.sdk/source/main/pluginfactory.h"
#include "public.sdk/source/common/pluginview.h"
#include "base/source/fstreamer.h"
#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "pluginterfaces/gui/iplugview.h"
#include <windows.h>
#include <commctrl.h>
#include <algorithm>
#include <cmath>
#include <cstdio>

using namespace Steinberg;
using namespace Steinberg::Vst;

extern "C" IMAGE_DOS_HEADER __ImageBase;

static const FUID kProcUID(0x7A4B1C01, 0x2E3D4F50, 0x61728394, 0xA5B6C7D8);
static const FUID kProc2UID(0x7A4B1C03, 0x2E3D4F50, 0x61728394, 0xA5B6C7D8);
static const FUID kCtrlUID(0x7A4B1C02, 0x2E3D4F50, 0x61728394, 0xA5B6C7D8);
enum : ParamID { kGainId = 0, kMeterId = 1, kCrashId = 2 };
static const int kDelay = 64;

// ============================================================================ processador
class TestProcessor : public AudioEffect
{
public:
    explicit TestProcessor(bool inv) : invert(inv) { setControllerClass(kCtrlUID); }
    static FUnknown* create(void*) { return (IAudioProcessor*)new TestProcessor(false); }
    static FUnknown* createInvert(void*) { return (IAudioProcessor*)new TestProcessor(true); }

    tresult PLUGIN_API initialize(FUnknown* ctx) override
    {
        tresult r = AudioEffect::initialize(ctx);
        if (r != kResultOk) return r;
        addAudioInput(STR16("Entrada"), SpeakerArr::kStereo);
        addAudioOutput(STR16("Saida"), SpeakerArr::kStereo);
        return kResultOk;
    }
    tresult PLUGIN_API setBusArrangements(SpeakerArrangement* ins, int32 nIn, SpeakerArrangement* outs, int32 nOut) override
    {
        if (nIn == 1 && nOut == 1 && ins[0] == outs[0] && (ins[0] == SpeakerArr::kMono || ins[0] == SpeakerArr::kStereo))
            return AudioEffect::setBusArrangements(ins, nIn, outs, nOut);
        return kResultFalse;
    }
    tresult PLUGIN_API canProcessSampleSize(int32 s) override { return s == kSample32 ? kResultTrue : kResultFalse; }
    uint32 PLUGIN_API getLatencySamples() override { return kDelay; }
    tresult PLUGIN_API setActive(TBool state) override
    {
        memset(ring, 0, sizeof(ring));
        pos = 0;
        return AudioEffect::setActive(state);
    }
    tresult PLUGIN_API process(ProcessData& d) override
    {
        if (IParameterChanges* pc = d.inputParameterChanges)
        {
            for (int32 i = 0; i < pc->getParameterCount(); i++)
            {
                IParamValueQueue* q = pc->getParameterData(i);
                int32 off = 0;
                ParamValue v = 0;
                if (!q || q->getPointCount() <= 0 || q->getPoint(q->getPointCount() - 1, off, v) != kResultTrue) continue;
                if (q->getParameterId() == kGainId) gain = v;
                if (q->getParameterId() == kCrashId) crash = v > 0.5;
            }
        }
        if (crash) *(volatile int*)nullptr = 42; // de proposito: o host tem que sobreviver
        if (d.numInputs < 1 || d.numOutputs < 1 || d.numSamples <= 0) return kResultOk;
        AudioBusBuffers& in = d.inputs[0];
        AudioBusBuffers& out = d.outputs[0];
        float g = (float)(gain * 2.0) * (invert ? -1.f : 1.f);
        float peak = 0;
        for (int32 c = 0; c < out.numChannels && c < 2; c++)
        {
            float* src = in.channelBuffers32[c < in.numChannels ? c : 0];
            float* dst = out.channelBuffers32[c];
            int p = pos;
            for (int32 i = 0; i < d.numSamples; i++)
            {
                float x = src[i];
                float y = ring[c][p];
                ring[c][p] = x;
                p = (p + 1) % kDelay;
                dst[i] = y * g;
                peak = std::max(peak, std::fabs(dst[i]));
            }
        }
        pos = (pos + d.numSamples) % kDelay;
        if (d.outputParameterChanges)
        {
            int32 idx = 0, pi = 0;
            if (IParamValueQueue* q = d.outputParameterChanges->addParameterData(kMeterId, idx))
                q->addPoint(0, std::min(1.0f, peak), pi);
        }
        return kResultOk;
    }
    tresult PLUGIN_API setState(IBStream* s) override
    {
        IBStreamer st(s, kLittleEndian);
        double v = 0;
        if (!st.readDouble(v)) return kResultFalse;
        gain = v;
        return kResultOk;
    }
    tresult PLUGIN_API getState(IBStream* s) override
    {
        IBStreamer st(s, kLittleEndian);
        st.writeDouble(gain);
        return kResultOk;
    }

    bool invert;
    bool crash = false;
    double gain = 0.5;
    float ring[2][kDelay] = {};
    int pos = 0;
};

// ============================================================================ controlador + janela
class TestController;

class TestView : public CPluginView
{
public:
    explicit TestView(TestController* c);
    ~TestView() override;
    tresult PLUGIN_API isPlatformTypeSupported(FIDString type) override
    {
        return strcmp(type, kPlatformTypeHWND) == 0 ? kResultTrue : kResultFalse;
    }
    tresult PLUGIN_API attached(void* parent, FIDString type) override;
    tresult PLUGIN_API removed() override;
    tresult PLUGIN_API canResize() override { return kResultTrue; }
    tresult PLUGIN_API onSize(ViewRect* r) override
    {
        if (wnd) SetWindowPos(wnd, nullptr, 0, 0, r->getWidth(), r->getHeight(), SWP_NOZORDER | SWP_NOMOVE);
        return CPluginView::onSize(r);
    }
    void UpdateGain();
    void UpdateMeter(double v);

    TestController* ctrl;
    HWND wnd = nullptr, slider = nullptr, meter = nullptr;
};

class TestController : public EditController
{
public:
    static FUnknown* create(void*) { return (IEditController*)new TestController(); }
    tresult PLUGIN_API initialize(FUnknown* ctx) override
    {
        tresult r = EditController::initialize(ctx);
        if (r != kResultOk) return r;
        parameters.addParameter(STR16("Ganho"), STR16("x"), 0, 0.5, ParameterInfo::kCanAutomate, kGainId);
        parameters.addParameter(STR16("Medidor"), nullptr, 0, 0, ParameterInfo::kIsReadOnly, kMeterId);
        parameters.addParameter(STR16("Travar"), nullptr, 1, 0, ParameterInfo::kCanAutomate, kCrashId);
        return kResultOk;
    }
    tresult PLUGIN_API setComponentState(IBStream* s) override
    {
        IBStreamer st(s, kLittleEndian);
        double v = 0;
        if (st.readDouble(v)) setParamNormalized(kGainId, v);
        return kResultOk;
    }
    tresult PLUGIN_API setParamNormalized(ParamID id, ParamValue v) override
    {
        tresult r = EditController::setParamNormalized(id, v);
        if (view && id == kGainId) view->UpdateGain();
        if (view && id == kMeterId) view->UpdateMeter(v);
        return r;
    }
    IPlugView* PLUGIN_API createView(FIDString name) override
    {
        if (!FIDStringsEqual(name, ViewType::kEditor)) return nullptr;
        return new TestView(this);
    }
    TestView* view = nullptr;
};

TestView::TestView(TestController* c) : CPluginView(nullptr), ctrl(c)
{
    ViewRect r(0, 0, 360, 120);
    setRect(r);
}
TestView::~TestView()
{
    if (ctrl && ctrl->view == this) ctrl->view = nullptr;
}

static LRESULT CALLBACK TestWndProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    auto* v = (TestView*)GetWindowLongPtrW(h, GWLP_USERDATA);
    if (m == WM_HSCROLL && v && (HWND)l == v->slider)
    {
        double val = (double)SendMessageW(v->slider, TBM_GETPOS, 0, 0) / 100.0;
        v->ctrl->beginEdit(kGainId);
        v->ctrl->EditController::setParamNormalized(kGainId, val);
        v->ctrl->performEdit(kGainId, val);
        v->ctrl->endEdit(kGainId);
        return 0;
    }
    if (m == WM_SIZE && v && v->slider) SetWindowPos(v->slider, nullptr, 10, 40, LOWORD(l) - 20, 30, SWP_NOZORDER);
    return DefWindowProcW(h, m, w, l);
}

tresult PLUGIN_API TestView::attached(void* parent, FIDString type)
{
    HINSTANCE hi = (HINSTANCE)&__ImageBase;
    static bool reg = false;
    if (!reg)
    {
        WNDCLASSW wc{};
        wc.lpfnWndProc = TestWndProc;
        wc.hInstance = hi;
        wc.lpszClassName = L"TVTestEditor";
        wc.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1);
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        RegisterClassW(&wc);
        INITCOMMONCONTROLSEX icc{sizeof(icc), ICC_BAR_CLASSES};
        InitCommonControlsEx(&icc);
        reg = true;
    }
    wnd = CreateWindowExW(0, L"TVTestEditor", L"", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, 0, rect.getWidth(), rect.getHeight(),
                          (HWND)parent, nullptr, hi, nullptr);
    SetWindowLongPtrW(wnd, GWLP_USERDATA, (LONG_PTR)this);
    CreateWindowExW(0, L"STATIC", L"TV Teste Ganho", WS_CHILD | WS_VISIBLE, 10, 10, 200, 20, wnd, nullptr, hi, nullptr);
    meter = CreateWindowExW(0, L"STATIC", L"Pico: -", WS_CHILD | WS_VISIBLE, 220, 10, 130, 20, wnd, (HMENU)102, hi, nullptr);
    slider = CreateWindowExW(0, TRACKBAR_CLASSW, L"", WS_CHILD | WS_VISIBLE | TBS_HORZ, 10, 40, rect.getWidth() - 20, 30, wnd, (HMENU)101, hi, nullptr);
    SendMessageW(slider, TBM_SETRANGE, TRUE, MAKELPARAM(0, 100));
    ctrl->view = this;
    UpdateGain();
    tresult r = CPluginView::attached(parent, type);
    if (plugFrame)
    {
        ViewRect nr(0, 0, 420, 180); // pede uma janela maior ao host
        plugFrame->resizeView(this, &nr);
    }
    return r;
}

tresult PLUGIN_API TestView::removed()
{
    if (wnd) DestroyWindow(wnd);
    wnd = slider = meter = nullptr;
    if (ctrl && ctrl->view == this) ctrl->view = nullptr;
    return CPluginView::removed();
}

void TestView::UpdateGain()
{
    if (slider) SendMessageW(slider, TBM_SETPOS, TRUE, (LPARAM)std::lround(ctrl->getParamNormalized(kGainId) * 100));
}
void TestView::UpdateMeter(double v)
{
    if (!meter) return;
    wchar_t buf[64];
    swprintf_s(buf, L"Pico: %.3f", v);
    SetWindowTextW(meter, buf);
}

// ============================================================================ fabrica
BEGIN_FACTORY_DEF("TakaVox Teste", "https://example.invalid", "mailto:teste@example.invalid")
    DEF_CLASS2(INLINE_UID_FROM_FUID(kProcUID), PClassInfo::kManyInstances, kVstAudioEffectClass, "TV Teste Ganho",
               Vst::kDistributable, "Fx", "1.0.0", kVstVersionString, TestProcessor::create)
    DEF_CLASS2(INLINE_UID_FROM_FUID(kProc2UID), PClassInfo::kManyInstances, kVstAudioEffectClass, "TV Teste Inverte",
               Vst::kDistributable, "Fx", "1.0.0", kVstVersionString, TestProcessor::createInvert)
    DEF_CLASS2(INLINE_UID_FROM_FUID(kCtrlUID), PClassInfo::kManyInstances, kVstComponentControllerClass, "TV Teste Controller",
               0, "", "1.0.0", kVstVersionString, TestController::create)
END_FACTORY
