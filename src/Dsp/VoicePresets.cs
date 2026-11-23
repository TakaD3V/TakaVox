using System;

namespace TakaVox.Dsp
{
    /// <summary>Uma voz pronta do modificador (so mexe nos ajustes Vc*; a cadeia de estudio continua a mesma).</summary>
    public sealed class VoicePreset
    {
        public readonly string Name, Emoji, Desc;
        readonly Action<ChainParams> set;

        public VoicePreset(string name, string emoji, string desc, Action<ChainParams> set)
        {
            Name = name; Emoji = emoji; Desc = desc; this.set = set;
        }

        /// <summary>Zera o modificador e aplica esta voz (nao liga nem desliga).</summary>
        public void ApplyTo(ChainParams p)
        {
            VoicePresets.ResetVoice(p);
            set(p);
            p.Sanitize();
        }
    }

    public static class VoicePresets
    {
        // Tom em semitons; timbre (formantes) em %. Voz feminina tem formantes ~15-20% mais altos que a masculina;
        // personagens de anime exageram (~25-30%), criancas mais ainda. Mudar so o tom soa "esquilo".
        // As vozes "de gente" ficam LIMPAS: sem cortar os graves (soa fino, de microfone barato) e com pouco sopro
        // (o sopro agora e ar com a forma da voz, nao chiado). Medido no tools/VoiceRealism.
        public static readonly VoicePreset[] All =
        {
            new VoicePreset("Garota de anime", "\U0001F380", "Voz fina e fofa de personagem de anime: tom bem mais alto e garganta menor, limpa e brilhante.",
                p => { p.VcPitch = 11; p.VcFormant = 22; p.VcIntonation = 115; p.VcBreath = 8; p.VcHighDb = 1.5f; }),
            new VoicePreset("Anime kawaii", "\U0001F338", "Ainda mais fina, doce e expressiva (estilo garota mágica).",
                p => { p.VcPitch = 12; p.VcFormant = 28; p.VcIntonation = 130; p.VcBreath = 10; p.VcHighDb = 2; }),
            new VoicePreset("Garota", "\U0001F469", "Voz feminina adulta e natural.",
                p => { p.VcPitch = 8; p.VcFormant = 17; p.VcIntonation = 110; p.VcBreath = 6; p.VcHighDb = 1; }),
            new VoicePreset("Criança", "\U0001F9D2", "Voz de criança pequena.",
                p => { p.VcPitch = 9; p.VcFormant = 30; p.VcIntonation = 120; p.VcHighDb = 1; }),
            new VoicePreset("Esquilo", "\U0001F43F", "O clássico 'voz de esquilo': tudo acelerado.",
                p => { p.VcPitch = 12; p.VcFormant = 55; p.VcIntonation = 110; }),
            new VoicePreset("Voz grossa", "\U0001F9D4", "Mais grave e encorpada, ainda natural.",
                p => { p.VcPitch = -4; p.VcFormant = -12; p.VcLowDb = 3; p.VcHighDb = -1; }),
            new VoicePreset("Herói épico", "\U0001F3AC", "Voz de trailer de filme: grave, firme e com ambiente de cinema.",
                p => { p.VcPitch = -2; p.VcFormant = -8; p.VcIntonation = 85; p.VcLowDb = 3; p.VcHighDb = 2; p.VcReverbMix = 18; p.VcReverbSize = 70; }),
            new VoicePreset("Gigante", "\U0001F5FF", "Enorme: tom e garganta muito maiores.",
                p => { p.VcPitch = -9; p.VcFormant = -25; p.VcIntonation = 80; p.VcLowDb = 4; p.VcReverbMix = 15; p.VcReverbSize = 80; }),
            new VoicePreset("Monstro", "\U0001F479", "Rosnado grave com uma segunda voz uma oitava abaixo.",
                p => { p.VcPitch = -7; p.VcFormant = -28; p.VcExtraMix = 55; p.VcExtraPitch = -12; p.VcExtraFormant = -35; p.VcDrive = 22; p.VcLowDb = 4; p.VcHighDb = -2; }),
            new VoicePreset("Demônio", "\U0001F608", "Voz sombria em camadas, com eco de catedral.",
                p => { p.VcPitch = -5; p.VcFormant = -18; p.VcExtraMix = 75; p.VcExtraPitch = -12; p.VcExtraFormant = -30; p.VcDrive = 10; p.VcReverbMix = 25; p.VcReverbSize = 85; }),
            new VoicePreset("Robô", "\U0001F916", "Monótono e metálico.",
                p => { p.VcIntonation = 0; p.VcMetal = 60; p.VcMetalHz = 110; p.VcRingMix = 20; p.VcRingHz = 55; p.VcHighDb = 2; }),
            new VoicePreset("Robô sintetizado", "\U0001F3B9", "Vocoder de sintetizador, estilo música eletrônica.",
                p => { p.VcSynth = 100; p.VcSynthHz = 110; p.VcReverbMix = 10; p.VcReverbSize = 40; }),
            new VoicePreset("Alien", "\U0001F47D", "Voz de outro planeta: oscilante e modulada.",
                p => { p.VcPitch = 5; p.VcFormant = 12; p.VcVibDepth = 70; p.VcVibRate = 7; p.VcRingMix = 35; p.VcRingHz = 300; p.VcEchoMix = 22; p.VcEchoMs = 180; p.VcEchoFb = 30; }),
            new VoicePreset("Fantasma", "\U0001F47B", "Sussurrada, trêmula e cheia de eco.",
                p => { p.VcPitch = 2; p.VcBreath = 75; p.VcVibDepth = 30; p.VcVibRate = 4; p.VcTremDepth = 25; p.VcTremRate = 5; p.VcEchoMix = 20; p.VcEchoMs = 350; p.VcEchoFb = 40; p.VcReverbMix = 45; p.VcReverbSize = 85; }),
            new VoicePreset("Sussurro", "\U0001F92B", "Só o sussurro, sem a voz.",
                p => { p.VcBreath = 100; p.VcLowDb = -4; p.VcHighDb = 4; }),
            new VoicePreset("Velhinho", "\U0001F474", "Voz idosa, trêmula e soprosa.",
                p => { p.VcPitch = -1; p.VcFormant = -4; p.VcIntonation = 110; p.VcVibDepth = 45; p.VcVibRate = 6.5f; p.VcTremDepth = 18; p.VcTremRate = 6.5f; p.VcBreath = 22; p.VcLowDb = -2; p.VcHighDb = -2; }),
            new VoicePreset("Dueto", "\U0001F3B6", "Você e uma voz fina uma oitava acima, ao mesmo tempo.",
                p => { p.VcExtraMix = 60; p.VcExtraPitch = 12; p.VcExtraFormant = 24; p.VcReverbMix = 12; p.VcReverbSize = 50; }),
            new VoicePreset("Rádio comunicador", "\U0001F4FB", "Walkie-talkie / rádio de missão.",
                p => { p.VcFilter = 2; p.VcDrive = 20; }),
            new VoicePreset("Telefone", "☎", "Ligação de celular antigo.",
                p => { p.VcFilter = 1; }),
            new VoicePreset("Megafone", "\U0001F4E2", "Alto-falante de estádio ou de protesto.",
                p => { p.VcFilter = 3; p.VcDrive = 25; p.VcEchoMix = 12; p.VcEchoMs = 120; p.VcEchoFb = 20; }),
            new VoicePreset("Caverna", "\U0001F987", "Eco enorme de caverna.",
                p => { p.VcReverbMix = 40; p.VcReverbSize = 95; p.VcEchoMix = 30; p.VcEchoMs = 420; p.VcEchoFb = 40; }),
            new VoicePreset("Abafado", "\U0001F9F1", "Como se estivesse atrás de uma parede.",
                p => { p.VcFilter = 4; p.VcReverbMix = 12; p.VcReverbSize = 30; }),
        };

        public static VoicePreset Find(string name)
        {
            foreach (var v in All) if (v.Name == name) return v;
            return null;
        }

        /// <summary>Volta todos os ajustes do modificador ao neutro (mantem o liga/desliga).</summary>
        public static void ResetVoice(ChainParams p)
        {
            bool on = p.VcOn;
            p.CopyVoiceFrom(new ChainParams());
            p.VcOn = on;
        }

        /// <summary>Os ajustes atuais nao mudam nada na voz?</summary>
        public static bool IsNeutral(ChainParams p) => p.SameVoice(new ChainParams());

        // vozes da 1a versao do modificador (com chiado de "ar" e graves cortados): quem esta com elas ganha a nova
        static readonly (string name, Action<ChainParams> set)[] OldVersions =
        {
            ("Garota de anime", p => { p.VcPitch = 11; p.VcFormant = 24; p.VcIntonation = 135; p.VcBreath = 10; p.VcLowDb = -4; p.VcHighDb = 3; }),
            ("Anime kawaii", p => { p.VcPitch = 12; p.VcFormant = 30; p.VcIntonation = 165; p.VcBreath = 15; p.VcLowDb = -5; p.VcHighDb = 4; }),
            ("Garota", p => { p.VcPitch = 7; p.VcFormant = 16; p.VcIntonation = 115; p.VcBreath = 6; p.VcLowDb = -2; p.VcHighDb = 2; }),
            ("Criança", p => { p.VcPitch = 9; p.VcFormant = 34; p.VcIntonation = 130; p.VcLowDb = -4; p.VcHighDb = 2; }),
        };

        /// <summary>Se os ajustes sao exatamente uma voz da versao antiga, troca pela versao nova (retorna o nome).</summary>
        public static string MigrateOld(ChainParams p)
        {
            var t = new ChainParams();
            foreach (var o in OldVersions)
            {
                ResetVoice(t);
                o.set(t);
                t.Sanitize();
                if (!t.SameVoice(p)) continue;
                var nv = Find(o.name);
                if (nv == null) return null;
                nv.ApplyTo(p);
                return o.name;
            }
            return null;
        }

        /// <summary>Qual voz pronta corresponde aos ajustes atuais (null = personalizada ou neutra).</summary>
        public static VoicePreset Match(ChainParams p)
        {
            var t = new ChainParams();
            foreach (var v in All)
            {
                v.ApplyTo(t);
                if (t.SameVoice(p)) return v;
            }
            return null;
        }
    }
}
