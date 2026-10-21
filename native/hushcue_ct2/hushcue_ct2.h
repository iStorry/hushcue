// hushcue_ct2: a small C interface over CTranslate2's Whisper model, so C# can call it with P/Invoke.
//
// Every function that can fail returns NULL (or -1) and leaves a message for hc_last_error() on the calling
// thread. Everything a function returns is owned by the caller and released with the matching hc_*_free.
#pragma once

#include <stddef.h>

#ifdef HUSHCUE_CT2_BUILD
#define HC_API __declspec(dllexport)
#else
#define HC_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct hc_model hc_model;     // a loaded Whisper model
typedef struct hc_encoded hc_encoded; // encoder output for one 30 s window, kept on the device

typedef struct hc_generate_options {
  int beam_size;
  float patience;
  float length_penalty;
  float repetition_penalty;
  int no_repeat_ngram_size;
  int max_length;
  int sampling_topk;
  float sampling_temperature;
  int num_hypotheses;
  int return_scores;
  int return_no_speech_prob;
  int max_initial_timestamp_index;
  int suppress_blank;
  const int* suppress_tokens; // NULL: suppress nothing
  int suppress_tokens_count;
} hc_generate_options;

typedef struct hc_generation {
  int hypotheses;
  const int* lengths; // tokens per hypothesis
  const int* ids;     // all hypotheses' token ids, one after the other
  const float* scores;
  float no_speech_prob;
} hc_generation;

typedef struct hc_alignment {
  int count;
  const int* text_indices;
  const int* time_indices;
  int probs_count;
  const float* text_token_probs;
} hc_alignment;

typedef struct hc_languages {
  int count;
  const char* const* tokens; // "<|en|>", most likely first
  const float* probs;
} hc_languages;

HC_API const char* hc_last_error(void);
HC_API int hc_cuda_device_count(void);

// device: "cuda" or "cpu"; compute_type: "float16", "int8", "default", ...
HC_API hc_model* hc_load(const char* model_path, const char* device, int device_index, const char* compute_type,
                         int inter_threads, int intra_threads);
HC_API void hc_free(hc_model* model);
HC_API int hc_is_multilingual(const hc_model* model);
HC_API int hc_n_mels(const hc_model* model);

// features: n_mels x n_frames log-mel values, row-major (one mel band after another)
HC_API hc_encoded* hc_encode(hc_model* model, const float* features, int n_mels, int n_frames);
HC_API void hc_encoded_free(hc_encoded* encoded);

HC_API hc_generation* hc_generate(hc_model* model, const hc_encoded* encoded, const int* prompt, int prompt_length,
                                  const hc_generate_options* options);
HC_API void hc_generation_free(hc_generation* generation);

HC_API hc_alignment* hc_align(hc_model* model, const hc_encoded* encoded, const int* start_sequence,
                              int start_length, const int* text_tokens, int text_length, int num_frames,
                              int median_filter_width);
HC_API void hc_alignment_free(hc_alignment* alignment);

HC_API hc_languages* hc_detect_language(hc_model* model, const hc_encoded* encoded);
HC_API void hc_languages_free(hc_languages* languages);

#ifdef __cplusplus
}
#endif
