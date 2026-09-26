from __future__ import annotations

import json
import os
import time
from typing import Any, Iterator

import requests

try:
    import certifi
except Exception:
    certifi = None


DEFAULT_GROQ_BASE_URL = "https://api.groq.com/openai/v1"
DEFAULT_GROQ_MODEL = "llama-3.1-8b-instant"


class GroqConfigurationError(RuntimeError):
    """Raised when an LLM call is requested without Groq credentials."""


def _load_dotenv_if_available() -> None:
    try:
        from dotenv import load_dotenv
    except Exception:
        return
    load_dotenv()


def _env_int(name: str, default: int) -> int:
    try:
        return int(os.getenv(name, str(default)))
    except ValueError:
        return default


def _ssl_verify_setting() -> str | bool:
    verify_env = os.getenv("GROQ_SSL_VERIFY", "").strip().lower()
    if verify_env in {"0", "false", "no", "off"}:
        return False
    if certifi is not None:
        try:
            return certifi.where()
        except Exception:
            return True
    return True


def _allow_insecure_ssl_fallback() -> bool:
    value = os.getenv("GROQ_SSL_ALLOW_INSECURE_FALLBACK", "1").strip().lower()
    return value in {"1", "true", "yes", "on"}


def _looks_like_placeholder(value: str | None) -> bool:
    if not value:
        return True
    lowered = value.strip().lower()
    return lowered in {
        "put_your_groq_api_key_here",
        "your_real_groq_api_key_here",
        "your_key_here",
        "put_your_groq_model_here",
        "your_selected_groq_model",
    }


class GroqClient:
    def __init__(
        self,
        api_key: str | None = None,
        model: str | None = None,
        base_url: str | None = None,
        timeout: int | None = None,
        max_retries: int = 2,
    ) -> None:
        _load_dotenv_if_available()
        self.api_key = api_key if api_key is not None else os.getenv("GROQ_API_KEY", "")
        self.model = model or os.getenv("GROQ_MODEL", DEFAULT_GROQ_MODEL)
        self.base_url = (base_url or os.getenv("GROQ_BASE_URL", DEFAULT_GROQ_BASE_URL)).rstrip("/")
        self.timeout = timeout or _env_int("GROQ_TIMEOUT", 60)
        self.max_retries = max(1, int(max_retries or 1))
        self.verify = _ssl_verify_setting()
        self.allow_insecure_ssl_fallback = _allow_insecure_ssl_fallback()

    def is_configured(self) -> bool:
        return not _looks_like_placeholder(self.api_key) and not _looks_like_placeholder(self.model)

    def _headers(self) -> dict[str, str]:
        if not self.is_configured():
            raise GroqConfigurationError(
                "Groq is not configured. Put your Groq API key in .env as GROQ_API_KEY=your_key_here."
            )
        return {
            "Authorization": f"Bearer {self.api_key}",
            "Content-Type": "application/json",
        }

    def generate_text(
        self,
        prompt: str,
        system: str = "",
        temperature: float = 0.1,
        max_tokens: int = 512,
        response_format: dict[str, str] | None = None,
    ) -> str:
        messages: list[dict[str, str]] = []
        if system:
            messages.append({"role": "system", "content": system})
        messages.append({"role": "user", "content": prompt})
        payload: dict[str, Any] = {
            "model": self.model,
            "messages": messages,
            "temperature": temperature,
            "max_tokens": max_tokens,
        }
        if response_format:
            payload["response_format"] = response_format

        last_error: Exception | None = None
        for attempt in range(self.max_retries):
            try:
                response = self._post_chat_completions(payload)
                data = response.json()
                return str(data["choices"][0]["message"]["content"]).strip()
            except requests.RequestException as exc:
                last_error = exc
                if attempt + 1 < self.max_retries:
                    time.sleep(0.4 * (attempt + 1))
            except (KeyError, IndexError, TypeError, ValueError) as exc:
                last_error = exc
                break
        if last_error:
            raise last_error
        return ""

    def stream_text(
        self,
        prompt: str,
        system: str = "",
        temperature: float = 0.1,
        max_tokens: int = 512,
    ) -> Iterator[str]:
        """Yield answer text incrementally from a streaming chat completion (Phase 2a).

        Mirrors ``generate_text`` but sets ``stream: True`` and parses the SSE
        ``data:`` lines, yielding each ``choices[0].delta.content`` chunk as it
        arrives. Because this is a generator, the body (including the
        ``GroqConfigurationError`` raised by ``_headers()``) runs on first
        iteration — callers should iterate inside a try/except and fall back to a
        non-streaming answer if the very first ``next()`` raises. Once tokens
        start flowing a mid-stream transport error simply ends the stream
        (fail-soft) rather than raising into an already-committed response.
        """
        messages: list[dict[str, str]] = []
        if system:
            messages.append({"role": "system", "content": system})
        messages.append({"role": "user", "content": prompt})
        payload: dict[str, Any] = {
            "model": self.model,
            "messages": messages,
            "temperature": temperature,
            "max_tokens": max_tokens,
            "stream": True,
        }
        response = self._post_chat_completions_stream(payload)
        try:
            for chunk in self._iter_sse_content(response):
                yield chunk
        finally:
            response.close()

    def _post_chat_completions_stream(self, payload: dict[str, Any]) -> requests.Response:
        request_kwargs = {
            "headers": self._headers(),
            "json": payload,
            "timeout": self.timeout,
            "verify": self.verify,
            "stream": True,
        }
        try:
            response = requests.post(f"{self.base_url}/chat/completions", **request_kwargs)
            response.raise_for_status()
            return response
        except requests.exceptions.SSLError as exc:
            ssl_error = "CERTIFICATE_VERIFY_FAILED" in str(exc).upper()
            if not ssl_error or self.verify is False or not self.allow_insecure_ssl_fallback:
                raise
            retry_kwargs = dict(request_kwargs)
            retry_kwargs["verify"] = False
            response = requests.post(f"{self.base_url}/chat/completions", **retry_kwargs)
            response.raise_for_status()
            return response

    @staticmethod
    def _iter_sse_content(response: requests.Response) -> Iterator[str]:
        """Parse an OpenAI/Groq-style SSE stream, yielding delta content strings.

        Skips keep-alive blanks and non-``data:`` lines, stops on ``[DONE]``, and
        tolerates a malformed chunk by skipping it rather than aborting the whole
        stream."""
        for raw_line in response.iter_lines(decode_unicode=True):
            if not raw_line:
                continue
            line = raw_line.strip()
            if not line.startswith("data:"):
                continue
            data = line[len("data:") :].strip()
            if data == "[DONE]":
                break
            try:
                parsed = json.loads(data)
            except json.JSONDecodeError:
                continue
            try:
                delta = parsed["choices"][0].get("delta") or {}
                content = delta.get("content")
            except (KeyError, IndexError, TypeError):
                continue
            if content:
                yield content

    def _post_chat_completions(self, payload: dict[str, Any]) -> requests.Response:
        request_kwargs = {
            "headers": self._headers(),
            "json": payload,
            "timeout": self.timeout,
            "verify": self.verify,
        }
        try:
            response = requests.post(f"{self.base_url}/chat/completions", **request_kwargs)
            response.raise_for_status()
            return response
        except requests.exceptions.SSLError as exc:
            ssl_error = "CERTIFICATE_VERIFY_FAILED" in str(exc).upper()
            if not ssl_error or self.verify is False or not self.allow_insecure_ssl_fallback:
                raise
            retry_kwargs = dict(request_kwargs)
            retry_kwargs["verify"] = False
            response = requests.post(f"{self.base_url}/chat/completions", **retry_kwargs)
            response.raise_for_status()
            return response

    def generate_json(
        self,
        prompt: str,
        system: str = "",
        temperature: float = 0,
        max_tokens: int = 512,
    ) -> dict[str, Any] | None:
        prompts = [
            prompt,
            (
                f"{prompt}\n\n"
                "Your previous response was not valid JSON. Return one JSON object only. "
                "No Markdown, no prose, no comments."
            ),
        ]
        for candidate_prompt in prompts:
            text = self.generate_text(
                prompt=candidate_prompt,
                system=system,
                temperature=temperature,
                max_tokens=max_tokens,
                response_format={"type": "json_object"},
            )
            parsed = self._parse_json_object(text)
            if parsed is not None:
                return parsed
        return None

    @staticmethod
    def _parse_json_object(text: str) -> dict[str, Any] | None:
        try:
            loaded = json.loads(text)
            return loaded if isinstance(loaded, dict) else None
        except json.JSONDecodeError:
            start = text.find("{")
            end = text.rfind("}")
            if start >= 0 and end > start:
                try:
                    loaded = json.loads(text[start : end + 1])
                    return loaded if isinstance(loaded, dict) else None
                except json.JSONDecodeError:
                    return None
        return None
