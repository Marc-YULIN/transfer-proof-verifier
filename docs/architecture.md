# Architecture

## Components

```mermaid
flowchart LR
    Merchant[Merchant backend] -->|API key| API[Minimal API]
    App[Capture app / curl] -->|Install token then session token| API
    API --> Tokens[Atomic token store: Redis / memory]
    API --> DB[(SQLite)]
    API --> Storage[Proof files]
    API --> Queue[Bounded channel: 100]
    Queue --> Worker[Verification worker]
    Worker --> Frames[ffmpeg + frame scorer]
    Frames --> Extractor[Gemini / OCR / embedded PNG]
    Extractor --> Match[Field matcher]
    Match --> DB
    Worker --> Storage
    Worker --> Signer[ES256 signer]
    Signer -->|Signed webhook| Merchant
    Merchant -->|Public JWKS| API
```

## Main sequence

```mermaid
sequenceDiagram
    participant M as Merchant backend
    participant S as Verifier API
    participant A as Capture app
    participant W as Verification worker
    participant D as SQLite
    M->>S: POST /api/orders (API key)
    M->>S: POST /api/capture-links
    S-->>M: proofcapture://start?token=...
    A->>S: POST /api/capture-sessions/exchange
    S->>S: Authenticate, expire check, atomic consume, hash compare
    S-->>A: Session ID + separate session token
    A->>S: POST /api/capture-sessions/{id}/proof
    S->>D: Conditional Issued → Uploaded + queued verification
    S-->>A: 202 + status URL
    W->>D: Conditional Queued → Processing
    W->>W: Extract frames → score → OCR / extraction
    W->>D: Persist each masked extraction attempt
    W->>W: Select most complete extraction, then confidence
    W->>D: Transaction: verification + session + optional dispute
    W->>M: JWS webhook with retry audit
    M->>S: GET /.well-known/jwks.json
    M->>M: Verify signature, iat, and deduplicate verificationId
```

## Capture session states

```mermaid
stateDiagram-v2
    [*] --> Issued: authenticated link exchanged
    Issued --> Uploaded: atomic upload accepted
    Issued --> Expired: expired session presented
    Uploaded --> Verifying: worker starts
    Verifying --> Completed: result committed
    Completed --> [*]
    Expired --> [*]
```

Expiry is checked on upload; no periodic expiry sweep runs. A failed worker leaves the session Verifying and the verification Failed for investigation.

## Verification states

```mermaid
stateDiagram-v2
    [*] --> Queued: file saved
    Queued --> Processing: conditional update claims work
    Processing --> Completed: result and dispute committed
    Processing --> Failed: processing exception
    Completed --> [*]
    Failed --> [*]
```

The queue and retry schedule are process-local. Restart recovery, a durable outbox, and automatic resumption of Processing work are future work. A completed result stays completed even when webhook delivery fails.

## Data model

```mermaid
erDiagram
    PaymentOrder ||--o{ CaptureSession : captures
    PaymentOrder ||--o{ Verification : verifies
    CaptureSession ||--o{ Verification : uploads
    Verification ||--o{ ExtractionAttempt : audits
    Verification ||--o| Dispute : opens
    Verification ||--o{ WebhookDelivery : notifies
    PaymentOrder {
        string Id PK
        string BuyerName
        string SellerName
        string SellerBank
        string SellerAccount
        long Amount "decimal scaled by 10000"
        string Currency
        datetime CreatedAt
    }
    CaptureSession {
        guid Id PK
        string OrderId FK
        string SessionTokenHash UK
        string Status
        datetime IssuedAt
        datetime ExpiresAt
        datetime UploadedAt
    }
    Verification {
        guid Id PK
        guid SessionId FK
        string OrderId FK
        string MediaKind
        string StoragePath
        string Status
        string Verdict
        string ChecksJson
        int BestFrameIndex
        string TraceId
        datetime CreatedAt
        datetime CompletedAt
    }
    ExtractionAttempt {
        guid Id PK
        guid VerificationId FK
        int FrameIndex
        string Provider
        bool Succeeded
        long DurationMs
        string ResponseExcerpt
        string Error
        string TraceId
        datetime CreatedAt
    }
    Dispute {
        guid Id PK
        string OrderId
        guid VerificationId FK
        string Reason
        string EvidencePath
        string Status
        datetime CreatedAt
    }
    WebhookDelivery {
        guid Id PK
        guid VerificationId FK
        string Url
        int Attempt
        int StatusCode
        string Error
        datetime SentAt
    }
```

Proof files and receipts contain synthetic personal data. Only account suffixes appear in match checks and extraction excerpts. Raw OCR, provider response bodies, session tokens, and API keys are not logged. Embedded PNG extraction is only for deterministic demonstrations: it cannot establish receipt authenticity. A Matched result means the extracted fields agree with the order, not that funds settled.
