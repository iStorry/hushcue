#define HUSHCUE_CT2_BUILD
#include "hushcue_ct2.h"

#include <exception>
#include <memory>
#include <string>
#include <vector>

#include <ctranslate2/devices.h>
#include <ctranslate2/models/whisper.h>
#include <ctranslate2/types.h>

namespace {
  thread_local std::string last_error;

  template <typename F>
  auto guarded(F&& body) -> decltype(body()) {
    try {
      last_error.clear();
      return body();
    } catch (const std::exception& e) {
      last_error = e.what();
    } catch (...) {
      last_error = "unknown error";
    }
    return {};
  }

  std::vector<size_t> to_ids(const int* ids, int count) {
    return std::vector<size_t>(ids, ids + count);
  }
}

struct hc_model {
  std::unique_ptr<ctranslate2::models::Whisper> pool;
};

struct hc_encoded {
  ctranslate2::StorageView view;
};

struct generation_storage : hc_generation {
  std::vector<int> lengths_storage;
  std::vector<int> ids_storage;
  std::vector<float> scores_storage;
};

struct alignment_storage : hc_alignment {
  std::vector<int> text_storage;
  std::vector<int> time_storage;
  std::vector<float> probs_storage;
};

struct languages_storage : hc_languages {
  std::vector<std::string> token_storage;
  std::vector<const char*> token_pointers;
  std::vector<float> prob_storage;
};

extern "C" {

const char* hc_last_error(void) {
  return last_error.c_str();
}

int hc_cuda_device_count(void) {
  try {
    return ctranslate2::get_device_count(ctranslate2::Device::CUDA);
  } catch (...) {
    return 0;
  }
}

// Same settings as faster-whisper's WhisperModel: inter_threads = num_workers, intra_threads = cpu_threads.
hc_model* hc_load(const char* model_path, const char* device, int device_index, const char* compute_type,
                  int inter_threads, int intra_threads) {
  return guarded([&]() -> hc_model* {
    ctranslate2::models::ModelLoader loader(model_path);
    loader.device = ctranslate2::str_to_device(device);
    loader.device_indices = {device_index};
    loader.compute_type = ctranslate2::str_to_compute_type(compute_type);
    loader.num_replicas_per_device = static_cast<size_t>(inter_threads);
    loader.use_flash_attention = false;
    loader.tensor_parallel = false;
    ctranslate2::ReplicaPoolConfig config;
    config.num_threads_per_replica = static_cast<size_t>(intra_threads);
    config.max_queued_batches = 0;
    auto model = std::make_unique<hc_model>();
    model->pool = std::make_unique<ctranslate2::models::Whisper>(loader, config);
    return model.release();
  });
}

void hc_free(hc_model* model) {
  delete model;
}

int hc_is_multilingual(const hc_model* model) {
  return guarded([&]() { return model->pool->is_multilingual() ? 1 : 0; });
}

int hc_n_mels(const hc_model* model) {
  return guarded([&]() { return static_cast<int>(model->pool->n_mels()); });
}

hc_encoded* hc_encode(hc_model* model, const float* features, int n_mels, int n_frames) {
  return guarded([&]() -> hc_encoded* {
    // A view over the caller's memory, like StorageView.from_array in the Python binding.
    ctranslate2::StorageView input({1, n_mels, n_frames}, const_cast<float*>(features), ctranslate2::Device::CPU);
    auto encoded = std::make_unique<hc_encoded>();
    encoded->view = model->pool->encode(input, /*to_cpu=*/false).get();
    return encoded.release();
  });
}

void hc_encoded_free(hc_encoded* encoded) {
  delete encoded;
}

hc_generation* hc_generate(hc_model* model, const hc_encoded* encoded, const int* prompt, int prompt_length,
                           const hc_generate_options* o) {
  return guarded([&]() -> hc_generation* {
    ctranslate2::models::WhisperOptions options;
    options.beam_size = static_cast<size_t>(o->beam_size);
    options.patience = o->patience;
    options.length_penalty = o->length_penalty;
    options.repetition_penalty = o->repetition_penalty;
    options.no_repeat_ngram_size = static_cast<size_t>(o->no_repeat_ngram_size);
    options.max_length = static_cast<size_t>(o->max_length);
    options.sampling_topk = static_cast<size_t>(o->sampling_topk);
    options.sampling_temperature = o->sampling_temperature;
    options.num_hypotheses = static_cast<size_t>(o->num_hypotheses);
    options.return_scores = o->return_scores != 0;
    options.return_no_speech_prob = o->return_no_speech_prob != 0;
    options.max_initial_timestamp_index = static_cast<size_t>(o->max_initial_timestamp_index);
    options.suppress_blank = o->suppress_blank != 0;
    if (o->suppress_tokens)
      options.suppress_tokens.assign(o->suppress_tokens, o->suppress_tokens + o->suppress_tokens_count);
    else
      options.suppress_tokens.clear();

    std::vector<std::vector<size_t>> prompts{to_ids(prompt, prompt_length)};
    auto futures = model->pool->generate(encoded->view, std::move(prompts), options);
    auto result = futures.at(0).get();

    auto out = std::make_unique<generation_storage>();
    for (const auto& sequence : result.sequences_ids) {
      out->lengths_storage.push_back(static_cast<int>(sequence.size()));
      for (size_t id : sequence)
        out->ids_storage.push_back(static_cast<int>(id));
    }
    out->scores_storage = result.scores;
    out->hypotheses = static_cast<int>(out->lengths_storage.size());
    out->lengths = out->lengths_storage.data();
    out->ids = out->ids_storage.data();
    out->scores = out->scores_storage.data();
    out->no_speech_prob = result.no_speech_prob;
    return out.release();
  });
}

void hc_generation_free(hc_generation* generation) {
  delete static_cast<generation_storage*>(generation);
}

hc_alignment* hc_align(hc_model* model, const hc_encoded* encoded, const int* start_sequence, int start_length,
                       const int* text_tokens, int text_length, int num_frames, int median_filter_width) {
  return guarded([&]() -> hc_alignment* {
    std::vector<std::vector<size_t>> tokens{to_ids(text_tokens, text_length)};
    auto futures = model->pool->align(encoded->view, to_ids(start_sequence, start_length), std::move(tokens),
                                      {static_cast<size_t>(num_frames)}, median_filter_width);
    auto result = futures.at(0).get();

    auto out = std::make_unique<alignment_storage>();
    for (const auto& [text_index, time_index] : result.alignments) {
      out->text_storage.push_back(static_cast<int>(text_index));
      out->time_storage.push_back(static_cast<int>(time_index));
    }
    out->probs_storage = result.text_token_probs;
    out->count = static_cast<int>(out->text_storage.size());
    out->text_indices = out->text_storage.data();
    out->time_indices = out->time_storage.data();
    out->probs_count = static_cast<int>(out->probs_storage.size());
    out->text_token_probs = out->probs_storage.data();
    return out.release();
  });
}

void hc_alignment_free(hc_alignment* alignment) {
  delete static_cast<alignment_storage*>(alignment);
}

hc_languages* hc_detect_language(hc_model* model, const hc_encoded* encoded) {
  return guarded([&]() -> hc_languages* {
    auto futures = model->pool->detect_language(encoded->view);
    auto result = futures.at(0).get();

    auto out = std::make_unique<languages_storage>();
    for (const auto& [token, prob] : result) {
      out->token_storage.push_back(token);
      out->prob_storage.push_back(prob);
    }
    for (const auto& token : out->token_storage)
      out->token_pointers.push_back(token.c_str());
    out->count = static_cast<int>(out->token_storage.size());
    out->tokens = out->token_pointers.data();
    out->probs = out->prob_storage.data();
    return out.release();
  });
}

void hc_languages_free(hc_languages* languages) {
  delete static_cast<languages_storage*>(languages);
}

}  // extern "C"
