# Trainer visual identity

This emblem is designed as the shared visual identity for a family of trainer applications. It does not contain the name, logo, or visual property of any specific game.

## Files

- `trainer-icon-master.png`: transparent master artwork for interfaces and promotional material.
- `trainer.ico`: multiresolution Windows executable and window icon.
- `trainer-icon-prompt.md`: source prompt for reproducing or adapting the identity.
- `../../tools/build_trainer_icon.ps1`: rebuilds the ICO at 16, 20, 24, 32, 40, 48, 64, 128, and 256 pixels.

## Reuse in another WPF project

Copy both image files and configure the project:

```xml
<ApplicationIcon>path\trainer.ico</ApplicationIcon>
```

Include the ICO as a WPF `Resource` and use the same resource in each window's `Icon` property.

To rebuild the ICO after editing the master PNG:

```powershell
.\tools\build_trainer_icon.ps1
```
