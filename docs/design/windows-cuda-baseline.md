# Windows CUDA compatibility baseline

The primary Windows x64 CUDA build must run on `gpu-box` with its existing hardware
and driver. Separate native backend builds may support other deployment baselines.
Each variant must identify its supported GPU architectures and driver requirements.
The deployment host defines compatibility. The GPU or toolkit on a build machine does
not define the deployment target.

## Measured deployment target

Read-only checks on 2026-10-02 report:

| Component | Observed value |
| --- | --- |
| GPU | NVIDIA GeForce RTX 3090 Ti, 24 GB |
| Compute capability | 8.6 (`sm_86`) |
| NVIDIA driver | 595.97, Windows WDDM |
| CUDA driver support reported by `nvidia-smi` | 13.2 |
| CUDA compiler | `nvcc.exe` absent from PATH; conventional CUDA toolkit directory absent |
| Existing llama.cpp runtime | `G:\AI\llama.cpp\b11238`, CUDA 13 cuBLAS and cudart DLLs |

The `nvidia-smi` CUDA value describes driver support. It does not identify an installed
compiler toolkit. Native compilation requires a compiler on the build machine; running
a compiled application does not require `nvcc` on the deployment host.

## Required behavior

- Every CUDA backend in the primary Windows build must supply kernels executable on compute capability 8.6.
  Architecture selection must remain independent of a build runner's installed GPU.
- CUDA runtime libraries, generated PTX and native libraries must work with driver
  595.97. Updating the GPU box's driver is not a prerequisite for accepting a build.
- DLL selection must satisfy the backend's runtime ABI. CUDA 12 and CUDA 13 DLL names
  are distinct dependencies, not interchangeable filenames.
- A Windows compatibility check must execute GPU kernels on the unchanged GPU box.
  Successful startup, model listing or CPU fallback does not satisfy this requirement.
- GPU benchmarks must confirm CUDA execution before reporting GPU inference speed.
- Linux ARM64 GB10 and Blackwell architecture requirements belong to separate build
  profiles. They do not set the Windows x64 compatibility floor.
- Additional build variants may require newer GPUs or drivers. Their requirements must
  remain explicit, and they must not replace or raise the primary 3090 Ti baseline.
- Inference applications must consume the native backend through its owning package.
  A corrected package must use a new immutable version. Hosts must not patch PTX
  headers or copy TensorSharp source into the application.

CUDA minor versions do not have to be identical across the driver and runtime.
[NVIDIA's minor-version compatibility documentation](https://docs.nvidia.com/deploy/cuda-compatibility/minor-version-compatibility.html)
allows some newer runtimes on an older driver within a major version. It also identifies
limits for newer features and PTX JIT. A CUDA 13.4 runtime alone does not prove a mismatch
with this driver. Generated kernels and actual GPU execution remain part of verification.

## Confirmed dependency blocker

The pinned `Darkspyre.TensorSharp.Backends.Cuda` 2.8.6.7 package contains
`tensorsharp_kernels.ptx` and `tensorsharp_dsv4_kernels.ptx` with PTX version 8.7 and
target `sm_120`. The RTX 3090 Ti cannot execute kernels requiring compute capability
12.0. The existing native smoke reports CUDA JIT error 218 and CPU fallback.

That package does not satisfy this baseline. Its replacement must contain correctly
compiled kernels compatible with compute capability 8.6 and the measured driver.
This document records the deployment requirement; it does not certify a replacement
package or change the GPU box.
