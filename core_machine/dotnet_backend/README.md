# SportsPlatform.Auth

The complete three-machine Docker deployment is documented in
[`deploy/README.md`](deploy/README.md). It separates the backend, chatbot, and FIBA
prediction model while keeping authenticated communication between them.

The root `docker-compose.yml` remains a small backend-only local development stack.
