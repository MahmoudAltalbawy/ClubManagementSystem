# FIBA PDF Coach Chatbot API

FastAPI microservice for basketball coaches. It receives FIBA PDF reports, extracts structured box-score data, builds PDF retrieval chunks, and answers questions through an API.

Groq is used first for query understanding/classification. Numeric/statistical answers still come from Pandas only. Groq must not calculate stats or invent numbers; it only decides the route and structured intent, answers RAG questions from retrieved PDF chunks, handles follow-up rewrite fallback, and optionally rewrites Pandas wording when enabled.

## Groq API Key

1. Copy `.env.example` to `.env`.
2. Put your Groq API key in `.env` as:

```env
GROQ_API_KEY=your_real_key_here
```

3. Set `GROQ_MODEL` to a model available in your Groq console.
4. Never commit `.env`.

The app also reads `GROQ_API_KEY` from deployment environment variables, so hosted platforms can inject the key without a local `.env` file.

`.env.example` contains:

```env
GROQ_API_KEY=put_your_groq_api_key_here
GROQ_MODEL=put_your_groq_model_here
GROQ_BASE_URL=https://api.groq.com/openai/v1
GROQ_TIMEOUT=60
FAST_ANALYTICS_BYPASS=0
ENABLE_ANALYTICS_LLM_FORMATTING=0
APP_ENV=production
```

Routing flags:

- `FAST_ANALYTICS_BYPASS=0`: default. Groq classifies first, then Pandas or RAG executes the route.
- `FAST_ANALYTICS_BYPASS=1`: deterministic parser handles obvious analytics before Groq for maximum speed.
- `ENABLE_ANALYTICS_LLM_FORMATTING=0`: default. Return the Pandas analytics template answer directly.
- `ENABLE_ANALYTICS_LLM_FORMATTING=1`: after Pandas calculates the answer, Groq may rewrite wording only.

## Run Locally

```powershell
pip install -r requirements.txt
copy .env.example .env
# edit .env and set GROQ_API_KEY plus GROQ_MODEL
uvicorn api.main:app --reload --host 0.0.0.0 --port 8000
```

Swagger docs:

```text
http://localhost:8000/docs
```

## Run With Docker

```powershell
copy .env.example .env
# edit .env and set GROQ_API_KEY plus GROQ_MODEL
docker compose up --build
```

The compose service is `fiba-chatbot-api`, exposes `8000:8000`, reads `env_file: .env`, and mounts `./data:/app/data`.

## API Endpoints

- `GET /health`
- `POST /projects`
- `POST /projects/{project_id}/pdfs`
- `POST /projects/{project_id}/rebuild`
- `POST /projects/{project_id}/ask`
- `GET /projects/{project_id}/status`

## Example Curl Commands

Create project:

```bash
curl -X POST http://localhost:8000/projects \
  -H "Content-Type: application/json" \
  -d '{"project_id":"demo"}'
```

Upload PDFs:

```bash
curl -X POST http://localhost:8000/projects/demo/pdfs \
  -F "files=@/path/to/report1.pdf" \
  -F "files=@/path/to/report2.pdf"
```

Rebuild extraction and retrieval index:

```bash
curl -X POST http://localhost:8000/projects/demo/rebuild
```

Ask a question:

```bash
curl -X POST http://localhost:8000/projects/demo/ask \
  -H "Content-Type: application/json" \
  -d '{
    "question":"top stealers",
    "team":"EGY",
    "session_id":"session-1",
    "debug":true
  }'
```

## Data Layout

```text
data/
  projects/
    {project_id}/
      pdfs/
      extracted/
        players_box_scores.csv
        pdf_chunks.csv
        chroma_pdf_index/
        chroma_chat_memory/
      chat_history.db
```

## How Answers Are Produced

- Groq is used first as a semantic analytics planner by default: it converts coach language into a structured route plus `analytics_recipe`.
- Pandas analytics handles all numeric/statistical calculations after classification.
- Groq never calculates analytics stats and never provides the final analytics numbers.
- Questions like `Who deserves more minutes?` are treated as underused high-impact player recommendations, not as rankings by total minutes played.
- Supported analytics recipes include `rank_by_metric`, `player_comparison`, `player_summary`, `opportunity_score`, `weighted_score`, `balanced_impact_score`, and `assist_turnover_context`.
- If a question requires unavailable data such as heart rate, the API returns an unsupported-data response instead of inventing an answer.
- Groq answers only from retrieved PDF chunks for general/RAG questions.
- Groq is used for follow-up rewrite fallback when deterministic rewrite cannot understand the follow-up.
- Groq can optionally format analytics wording only when `ENABLE_ANALYTICS_LLM_FORMATTING=1`.
- Chroma is used for semantic PDF retrieval and semantic chat memory when available.
- TF-IDF retrieval is used as a fallback if Chroma, embeddings, or sentence-transformers are unavailable.
- Missing `GROQ_API_KEY` falls back to deterministic classification. Analytics questions still work, and RAG questions fall back gracefully instead of crashing.

To switch between modes:

```env
# Groq-first routing
FAST_ANALYTICS_BYPASS=0

# Old fast deterministic analytics bypass
FAST_ANALYTICS_BYPASS=1
```

## Legacy Streamlit UI

The original Streamlit UI is still available for local exploration:

```powershell
streamlit run app.py
```

It uses the same Groq environment variables instead of a local Ollama runtime.
