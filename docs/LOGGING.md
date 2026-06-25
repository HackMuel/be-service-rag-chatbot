# Logging & Trace — be_service

Observability sistem saat ini berbasis **structured logging** (belum ada dashboard metrics).
Setiap jalur retrieval/answer/ingestion menulis log bertanda (prefix) agar mudah di-`grep`.

## `RETRIEVAL_TRACE`

Menjelaskan jalur retrieval yang dipilih.

```text
RETRIEVAL_TRACE answerLevel=SemanticGrounded mode=semantic source=qdrant_vector_payload qdrantVectorSearch=True chunks=3
```

- `answerLevel` — Level 1/2/3 yang dipilih.
- `mode` — mode teknis retrieval.
- `source` — sumber retrieval (`qdrant_payload` | `qdrant_vector_payload`).
- `qdrantVectorSearch` — apakah vector search Qdrant dipakai.
- `chunks` — jumlah chunk final.

## `ANSWER_TRACE`

Menjelaskan cara jawaban dibuat.

```text
ANSWER_TRACE answerLevel=DeterministicTemplate retrievalMode=sop source=qdrant_payload qdrantVectorSearch=False usedDeterministicAnswer=True usedLlm=False
```

- `usedDeterministicAnswer=True` — jawaban dibuat oleh formatter.
- `usedLlm=True` — jawaban dibuat oleh Ollama LLM.

## `SEMANTIC_FILTER`

Menjelaskan filtering context semantic sebelum masuk LLM.

```text
SEMANTIC_FILTER before=5 after=3 allowedTypes=sop,audit,profile
```

## `STRUCTURED_ENTITY_RESOLVER`

Menjelaskan source known entities (cache vs refresh dari Qdrant payload).

```text
STRUCTURED_ENTITY_RESOLVER source=qdrant_payload_cache count=120
STRUCTURED_ENTITY_RESOLVER source=qdrant_payload_refresh count=120
```

## `STRUCTURED_ENTITY_MATCH`

Hasil entity match tanpa membocorkan content panjang.

```text
STRUCTURED_ENTITY_MATCH field=name matchType=fuzzy score=0.96
```

## `INGESTION_STORAGE_MODE`

Menjelaskan apakah `document_chunks` ditulis ke PostgreSQL.

```text
INGESTION_STORAGE_MODE writeDocumentChunksToPostgres=false
```

## `OBJECT_STORAGE_UPLOAD`

Upload file asli ke Object Storage.

```text
OBJECT_STORAGE_UPLOAD bucket=rag-documents objectKey=documents/... contentType=application/pdf
```

## Membaca kombinasi umum

- `source=qdrant_vector_payload` + `qdrantVectorSearch=True`
  → semantic search memakai Qdrant dan langsung memakai payload Qdrant sebagai context.
- `source=qdrant_payload` + `qdrantVectorSearch=False`
  → retrieval memakai filter payload Qdrant tanpa vector search (jalur eksak/template).
