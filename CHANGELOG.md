# Changelog

## v0.1.4 - 2026-05-13

- Added a recommendation-first start screen that shows the most useful branch-switch options instead of presenting every tool equally.
- Added the native Branch Switch Assistant for reversible Steam rediscovery workflows:
  - parks the currently live Conan folder safely,
  - waits for Steam uninstall state to register,
  - watches the manifest once per second,
  - confirms the intended branch when possible,
  - restores the parked target folder for Steam verification or rediscovery.
- Added uninstall and branch-selection reference images inside the wizard, plus clearer guidance for opening Steam `Properties` before choosing a branch.
- Added plain-language `parked` and `live` glossary entries so the wizard terminology stays understandable.
- Added a note about Steam occasionally queueing an odd roughly `70 GB` Workshop transfer after branch verification; players can stop that queue once Conan itself has verified correctly.
- Improved Steam library selection when more than one library contains Conan-related metadata.
- Added a Steam file-validation shortcut for the currently managed Conan install.
- Included rediscovery and manifest-inspection helper scripts plus Twilight Mire mod manifest tooling for support work.
- Hardened restore boundaries and kept the wording around reversible file actions more explicit.
