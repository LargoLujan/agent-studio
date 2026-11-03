"""Reference of the embeddings spike (2026-10-30): the token ids and the embeddings the .NET harness must reproduce.

Two references of the tokenization of intfloat/multilingual-e5-small:
  * tokenizers (Hugging Face, Rust) over onnx/tokenizer.json: the tokenizer the model card uses;
  * sentencepiece over onnx/sentencepiece.bpe.model, with the fairseq rule of XLM-RoBERTa: <s>=0, <pad>=1, </s>=2,
    <unk>=3 and every other piece shifted by one.
And the embeddings of every sentence with onnxruntime (fp32 and int8): mean pooling over the attention mask and L2
normalisation, as the model card says.

Usage (model files downloaded by the harness README, never committed):
  python -I reference.py --model-dir <folder with onnx/> --data data --out reference.json
"""

import argparse
import json
import pathlib

import numpy as np
import onnxruntime as ort
import sentencepiece as spm
import tokenizers

MAX_TOKENS = 512


def spm_ids(processor, text):
    pieces = processor.encode(text)
    mapped = [3 if i == processor.unk_id() else i + 1 for i in pieces]
    return [0] + mapped[: MAX_TOKENS - 2] + [2]


def embed(session, ids):
    input_ids = np.array([ids], dtype=np.int64)
    mask = np.ones_like(input_ids)
    types = np.zeros_like(input_ids)
    hidden = session.run(["last_hidden_state"], {"input_ids": input_ids, "attention_mask": mask, "token_type_ids": types})[0]
    pooled = (hidden * mask[..., None]).sum(axis=1) / mask.sum(axis=1, keepdims=True)
    vector = pooled[0]
    return (vector / np.linalg.norm(vector)).astype(np.float32).tolist()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--data", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    onnx = pathlib.Path(args.model_dir) / "onnx"
    sentences = json.loads((pathlib.Path(args.data) / "frases-tokenizacion.json").read_text(encoding="utf-8"))

    fast = tokenizers.Tokenizer.from_file(str(onnx / "tokenizer.json"))
    fast.enable_truncation(MAX_TOKENS)
    fast.no_padding()
    processor = spm.SentencePieceProcessor(model_file=str(onnx / "sentencepiece.bpe.model"))

    fp32 = ort.InferenceSession(str(onnx / "model.onnx"), providers=["CPUExecutionProvider"])
    int8 = ort.InferenceSession(str(onnx / "model_qint8_avx512_vnni.onnx"), providers=["CPUExecutionProvider"])

    rows = []
    for text in sentences:
        hf = fast.encode(text).ids
        rows.append({
            "text": text,
            "hf": hf,
            "spm": spm_ids(processor, text),
            "fp32": embed(fp32, hf),
            "int8": embed(int8, hf),
        })

    agree = sum(1 for r in rows if r["hf"] == r["spm"])
    print(f"tokenizers {tokenizers.__version__}, sentencepiece {spm.__version__}, onnxruntime {ort.__version__}")
    print(f"{len(rows)} sentences; tokenizers and sentencepiece agree on {agree}")
    for r in rows:
        if r["hf"] != r["spm"]:
            print("  differ:", repr(r["text"][:80]), len(r["hf"]), len(r["spm"]))

    pathlib.Path(args.out).write_text(json.dumps({
        "versions": {"tokenizers": tokenizers.__version__, "sentencepiece": spm.__version__, "onnxruntime": ort.__version__},
        "sentences": rows,
    }, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
