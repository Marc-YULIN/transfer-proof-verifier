# Synthetic transfer samples

Regenerate the receipts immediately before a walkthrough so their timestamp fits the order time window. All names and accounts are fictional.

| File | Expected outcome |
| --- | --- |
| transfer-matched.png | Matched for 150000 TWD, Alex Chen to Jamie Lin, account suffix 7890 |
| transfer-wrong-amount.png | Mismatched with an open dispute |
| transfer-loading-only.png | Unreadable with an open dispute; no frames reach extraction |
| transfer-matched.mp4 | Loading frames rejected; completion frames matched when OCR or Gemini is enabled |

```sh
dotnet run --project tools/TransferProof.SampleGenerator --no-restore -- --out samples --name transfer-matched --amount 150000 --sender "Alex Chen" --receiver "Jamie Lin" --bank "Demo Bank" --account "110-234-567890" --video
dotnet run --project tools/TransferProof.SampleGenerator --no-restore -- --out samples --name transfer-wrong-amount --amount 149999
dotnet run --project tools/TransferProof.SampleGenerator --no-restore -- --out samples --name transfer-loading-only --loading-only
```

Optional flags: `--currency TWD`, `--time 2026-11-12T00:00:00Z`, `--no-metadata`. PNG metadata is a demo-only provider and can be forged. Encoding to MP4 discards PNG metadata: Embedded mode returns Unreadable for video. Use OCR or Gemini for actual frame extraction demonstrations. JPEG similarly requires OCR or Gemini.
