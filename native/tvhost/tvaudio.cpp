// Arquivos de audio para o sound pad do TakaVox, com o Media Foundation do proprio Windows
// (nada para instalar): le MP3, WAV, M4A/AAC, WMA, FLAC, ALAC... e grava MP3/M4A (testes e gravacoes).
#include <windows.h>
#include <objbase.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <algorithm>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "ole32.lib")

#define TVA_API extern "C" __declspec(dllexport)

namespace
{
    std::once_flag gMfOnce;
    HRESULT gMfHr = E_FAIL;

    void SetErr(wchar_t* err, int cap, const wchar_t* msg, HRESULT hr = S_OK)
    {
        if (!err || cap <= 0) return;
        wchar_t buf[512];
        if (hr != S_OK) swprintf_s(buf, L"%s (0x%08X)", msg, (unsigned)hr);
        else wcsncpy_s(buf, msg, _TRUNCATE);
        wcsncpy_s(err, cap, buf, _TRUNCATE);
    }

    // COM no modo da thread (o .NET chama de threads do pool, que ja sao MTA; se for STA, segue assim mesmo)
    struct ComScope
    {
        bool undo = false;
        ComScope() { HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED); undo = SUCCEEDED(hr); }
        ~ComScope() { if (undo) CoUninitialize(); }
    };

    bool StartMf(wchar_t* err, int cap)
    {
        std::call_once(gMfOnce, [] { gMfHr = MFStartup(MF_VERSION, MFSTARTUP_LITE); });
        if (FAILED(gMfHr)) { SetErr(err, cap, L"o Media Foundation do Windows não está disponível", gMfHr); return false; }
        return true;
    }

    template <class T> void Release(T*& p) { if (p) { p->Release(); p = nullptr; } }

    int DecodeImpl(const wchar_t* path, float** outData, long long* outFrames, int* outCh, int* outRate, int maxSeconds, wchar_t* err, int cap)
    {
        ComScope com;
        if (!StartMf(err, cap)) return 0;
        IMFSourceReader* reader = nullptr;
        IMFMediaType* want = nullptr;
        IMFMediaType* cur = nullptr;
        int result = 0;
        HRESULT hr = MFCreateSourceReaderFromURL(path, nullptr, &reader);
        if (FAILED(hr)) { SetErr(err, cap, L"arquivo de áudio inválido ou formato não suportado", hr); return 0; }
        do
        {
            reader->SetStreamSelection((DWORD)MF_SOURCE_READER_ALL_STREAMS, FALSE);
            hr = reader->SetStreamSelection((DWORD)MF_SOURCE_READER_FIRST_AUDIO_STREAM, TRUE);
            if (FAILED(hr)) { SetErr(err, cap, L"o arquivo não tem áudio", hr); break; }
            MFCreateMediaType(&want);
            want->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            want->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_Float);
            hr = reader->SetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_AUDIO_STREAM, nullptr, want);
            if (FAILED(hr)) { SetErr(err, cap, L"o Windows não tem decodificador para este formato", hr); break; }
            hr = reader->GetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_AUDIO_STREAM, &cur);
            if (FAILED(hr)) { SetErr(err, cap, L"formato de áudio desconhecido", hr); break; }
            UINT32 ch = MFGetAttributeUINT32(cur, MF_MT_AUDIO_NUM_CHANNELS, 0);
            UINT32 sr = MFGetAttributeUINT32(cur, MF_MT_AUDIO_SAMPLES_PER_SECOND, 0);
            UINT32 bits = MFGetAttributeUINT32(cur, MF_MT_AUDIO_BITS_PER_SAMPLE, 0);
            if (ch == 0 || ch > 16 || sr < 4000 || sr > 768000 || bits != 32) { SetErr(err, cap, L"formato de áudio não suportado"); break; }
            size_t maxSamples = (size_t)(maxSeconds > 0 ? maxSeconds : 600) * sr * ch;
            std::vector<float> out;
            out.reserve(std::min<size_t>(maxSamples, (size_t)sr * ch * 30));
            bool ok = true;
            for (;;)
            {
                DWORD flags = 0;
                IMFSample* s = nullptr;
                hr = reader->ReadSample((DWORD)MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, nullptr, &flags, nullptr, &s);
                if (FAILED(hr)) { if (out.empty()) { SetErr(err, cap, L"falha ao ler o áudio", hr); ok = false; } break; }
                if (s)
                {
                    IMFMediaBuffer* b = nullptr;
                    if (SUCCEEDED(s->ConvertToContiguousBuffer(&b)))
                    {
                        BYTE* p = nullptr;
                        DWORD len = 0;
                        if (SUCCEEDED(b->Lock(&p, nullptr, &len)))
                        {
                            size_t n = len / sizeof(float);
                            if (out.size() + n > maxSamples) n = maxSamples - out.size();
                            out.insert(out.end(), (float*)p, (float*)p + n);
                            b->Unlock();
                        }
                        Release(b);
                    }
                    Release(s);
                }
                if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
                if (flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) break; // muda no meio (raro): fica com o que leu
                if (out.size() >= maxSamples) break;
            }
            if (!ok) break;
            size_t frames = out.size() / ch;
            if (frames == 0) { SetErr(err, cap, L"o arquivo está vazio (sem áudio)"); break; }
            float* data = (float*)malloc(frames * ch * sizeof(float));
            if (!data) { SetErr(err, cap, L"sem memória"); break; }
            memcpy(data, out.data(), frames * ch * sizeof(float));
            *outData = data;
            *outFrames = (long long)frames;
            *outCh = (int)ch;
            *outRate = (int)sr;
            result = 1;
        } while (false);
        Release(cur);
        Release(want);
        Release(reader);
        return result;
    }

    int DecodeGuard(const wchar_t* path, float** d, long long* f, int* c, int* r, int maxS, wchar_t* err, int cap, bool* crashed)
    {
        __try { return DecodeImpl(path, d, f, c, r, maxS, err, cap); }
        __except (EXCEPTION_EXECUTE_HANDLER) { *crashed = true; return 0; }
    }

    int EncodeImpl(const wchar_t* path, const float* mono, long long frames, int rate, int kbps, wchar_t* err, int cap)
    {
        ComScope com;
        if (!StartMf(err, cap)) return 0;
        std::wstring p(path);
        std::wstring ext = p.size() > 4 ? p.substr(p.size() - 4) : L"";
        for (auto& c : ext) c = (wchar_t)towlower(c);
        GUID fmt;
        if (ext == L".mp3") fmt = MFAudioFormat_MP3;
        else if (ext == L".m4a") fmt = MFAudioFormat_AAC;
        else { SetErr(err, cap, L"use .mp3 ou .m4a"); return 0; }
        IMFSinkWriter* w = nullptr;
        IMFMediaType* outT = nullptr;
        IMFMediaType* inT = nullptr;
        DWORD stream = 0;
        int result = 0;
        HRESULT hr = MFCreateSinkWriterFromURL(path, nullptr, nullptr, &w);
        if (FAILED(hr)) { SetErr(err, cap, L"não deu para criar o arquivo", hr); return 0; }
        do
        {
            MFCreateMediaType(&outT);
            outT->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            outT->SetGUID(MF_MT_SUBTYPE, fmt);
            outT->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, (UINT32)rate);
            outT->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 1);
            if (fmt == MFAudioFormat_AAC)
            {
                outT->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                outT->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (UINT32)(kbps * 125 >= 16000 ? 16000 : 12000));
            }
            else
            {
                // o codificador MP3 do Windows so aceita taxas de bits padrao e alinhamento 1
                static const int rates[] = {32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320};
                int pick = 128;
                for (int r : rates) if (r <= kbps) pick = r;
                outT->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (UINT32)(pick * 125));
                outT->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 1);
            }
            hr = w->AddStream(outT, &stream);
            if (FAILED(hr)) { SetErr(err, cap, L"o Windows não tem codificador para este formato nessa taxa", hr); break; }
            MFCreateMediaType(&inT);
            inT->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            inT->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
            inT->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, (UINT32)rate);
            inT->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 1);
            inT->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            inT->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 2);
            inT->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (UINT32)rate * 2);
            hr = w->SetInputMediaType(stream, inT, nullptr);
            if (FAILED(hr)) { SetErr(err, cap, L"o codificador recusou a entrada", hr); break; }
            hr = w->BeginWriting();
            if (FAILED(hr)) { SetErr(err, cap, L"falha ao começar a gravar", hr); break; }
            const long long chunk = rate / 10;
            bool ok = true;
            for (long long pos = 0; pos < frames && ok; pos += chunk)
            {
                long long n = std::min<long long>(chunk, frames - pos);
                IMFMediaBuffer* b = nullptr;
                IMFSample* s = nullptr;
                MFCreateMemoryBuffer((DWORD)(n * 2), &b);
                BYTE* d = nullptr;
                b->Lock(&d, nullptr, nullptr);
                int16_t* o = (int16_t*)d;
                for (long long i = 0; i < n; i++)
                {
                    float v = mono[pos + i];
                    v = v > 1.f ? 1.f : (v < -1.f ? -1.f : v);
                    o[i] = (int16_t)(v * 32767.f);
                }
                b->Unlock();
                b->SetCurrentLength((DWORD)(n * 2));
                MFCreateSample(&s);
                s->AddBuffer(b);
                s->SetSampleTime(pos * 10000000LL / rate);
                s->SetSampleDuration(n * 10000000LL / rate);
                if (FAILED(w->WriteSample(stream, s))) ok = false;
                Release(s);
                Release(b);
            }
            if (!ok) { SetErr(err, cap, L"falha ao gravar"); break; }
            hr = w->Finalize();
            if (FAILED(hr)) { SetErr(err, cap, L"falha ao fechar o arquivo", hr); break; }
            result = 1;
        } while (false);
        Release(inT);
        Release(outT);
        Release(w);
        return result;
    }

    int EncodeGuard(const wchar_t* path, const float* mono, long long frames, int rate, int kbps, wchar_t* err, int cap, bool* crashed)
    {
        __try { return EncodeImpl(path, mono, frames, rate, kbps, err, cap); }
        __except (EXCEPTION_EXECUTE_HANDLER) { *crashed = true; return 0; }
    }
}

/// Decodifica um arquivo de audio inteiro (ate maxSeconds) para float intercalado na taxa original.
/// Retorna 1 ok (liberar com tvh_audio_free) ou 0 com o motivo em err.
TVA_API int tvh_audio_decode(const wchar_t* path, float** data, long long* frames, int* channels, int* rate, int maxSeconds, wchar_t* err, int errCap)
{
    *data = nullptr; *frames = 0; *channels = 0; *rate = 0;
    bool crashed = false;
    int r = DecodeGuard(path, data, frames, channels, rate, maxSeconds, err, errCap, &crashed);
    if (crashed) SetErr(err, errCap, L"o decodificador do Windows travou com este arquivo");
    return r;
}

TVA_API void tvh_audio_free(float* data) { free(data); }

/// Grava audio mono (float) em .mp3 ou .m4a com o codificador do Windows. Retorna 1 ok ou 0 com o motivo em err.
TVA_API int tvh_audio_encode(const wchar_t* path, const float* mono, long long frames, int rate, int kbps, wchar_t* err, int errCap)
{
    bool crashed = false;
    int r = EncodeGuard(path, mono, frames, rate, kbps, err, errCap, &crashed);
    if (crashed) SetErr(err, errCap, L"o codificador do Windows travou");
    return r;
}
