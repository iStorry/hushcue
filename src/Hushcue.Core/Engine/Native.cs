using System.Runtime.InteropServices;

namespace Hushcue.Core.Engine;

/// <summary>P/Invoke declarations for native/hushcue_ct2 (a C interface over CTranslate2's Whisper model).</summary>
internal static partial class Native
{
    private const string Library = "hushcue_ct2";

    [StructLayout(LayoutKind.Sequential)]
    public struct GenerateOptions
    {
        public int BeamSize;
        public float Patience;
        public float LengthPenalty;
        public float RepetitionPenalty;
        public int NoRepeatNgramSize;
        public int MaxLength;
        public int SamplingTopK;
        public float SamplingTemperature;
        public int NumHypotheses;
        public int ReturnScores;
        public int ReturnNoSpeechProb;
        public int MaxInitialTimestampIndex;
        public int SuppressBlank;
        public unsafe int* SuppressTokens;
        public int SuppressTokensCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Generation
    {
        public int Hypotheses;
        public int* Lengths;
        public int* Ids;
        public float* Scores;
        public float NoSpeechProb;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Alignment
    {
        public int Count;
        public int* TextIndices;
        public int* TimeIndices;
        public int ProbsCount;
        public float* TextTokenProbs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Languages
    {
        public int Count;
        public byte** Tokens;
        public float* Probs;
    }

    [LibraryImport(Library, EntryPoint = "hc_last_error")]
    public static partial IntPtr LastError();

    [LibraryImport(Library, EntryPoint = "hc_cuda_device_count")]
    public static partial int CudaDeviceCount();

    [LibraryImport(Library, EntryPoint = "hc_load", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr Load(string modelPath, string device, int deviceIndex, string computeType,
                                      int interThreads, int intraThreads);

    [LibraryImport(Library, EntryPoint = "hc_free")]
    public static partial void Free(IntPtr model);

    [LibraryImport(Library, EntryPoint = "hc_is_multilingual")]
    public static partial int IsMultilingual(IntPtr model);

    [LibraryImport(Library, EntryPoint = "hc_n_mels")]
    public static partial int NMels(IntPtr model);

    [LibraryImport(Library, EntryPoint = "hc_encode")]
    public static unsafe partial IntPtr Encode(IntPtr model, float* features, int nMels, int nFrames);

    [LibraryImport(Library, EntryPoint = "hc_encoded_free")]
    public static partial void EncodedFree(IntPtr encoded);

    [LibraryImport(Library, EntryPoint = "hc_generate")]
    public static unsafe partial Generation* Generate(IntPtr model, IntPtr encoded, int* prompt, int promptLength,
                                                     GenerateOptions* options);

    [LibraryImport(Library, EntryPoint = "hc_generation_free")]
    public static unsafe partial void GenerationFree(Generation* generation);

    [LibraryImport(Library, EntryPoint = "hc_align")]
    public static unsafe partial Alignment* Align(IntPtr model, IntPtr encoded, int* startSequence, int startLength,
                                                 int* textTokens, int textLength, int numFrames, int medianFilterWidth);

    [LibraryImport(Library, EntryPoint = "hc_alignment_free")]
    public static unsafe partial void AlignmentFree(Alignment* alignment);

    [LibraryImport(Library, EntryPoint = "hc_detect_language")]
    public static unsafe partial Languages* DetectLanguage(IntPtr model, IntPtr encoded);

    [LibraryImport(Library, EntryPoint = "hc_languages_free")]
    public static unsafe partial void LanguagesFree(Languages* languages);

    public static string ErrorMessage() => Marshal.PtrToStringUTF8(LastError()) ?? "unknown error";
}
