# Supra metadata fixture

`supra-metadata.json` contains only tensor names, dtypes, dimensions and two integer configuration
values. It was extracted with the restricted TensorSharp metadata readers from the installed,
previously verified built-in Supra pack. No weight values, tokenizer vocabulary or executable
repository code is included. Tests construct tiny metadata-only files from this independent snapshot;
the compatibility policy does not supply their expected shapes.

Sources (the full-artifact SHA-256 values are also pinned by `TensorImageModels.Supra2Image`):

- Supra DiT: `SupraLabs/Supra2-IMG`, revision `10dec6e4b4b5d1c44fd1d7d3fe5e50137333da5b`,
  `model_final_ema.pt`, Apache-2.0, SHA-256 `e96aa0537e0fd120af82b3a606dd50c90ae079ac7aa97b38a8da4882daeeee48`.
- Flan-T5: `google/flan-t5-base`, revision `7bcac572ce56db69c1ea7c8af255c5d7c9672fc2`,
  `model.safetensors`, Apache-2.0, SHA-256 `1dfb70afdcedceb9f9fae2f9b68e004ad934361fb35b9b2bd50b45ea90790fc8`.
- VAE: `stabilityai/sd-vae-ft-mse`, revision `31f26fdeee1355a5c34592e401dd41e45d25a493`,
  `diffusion_pytorch_model.safetensors`, MIT, SHA-256 `a1d993488569e928462932c8c38a0760b874d166399b14414135bd9c42df5815`.

This fixture proves metadata compatibility, not inference output parity or device availability.
