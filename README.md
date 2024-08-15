# Project-wide VRC Constraints Converter

This tool is used to convert Unity constraints to VRC Constraints for all assets in your project.

In the tool window, you can exclude some folders or assets from the conversion process.

**This project is under development**

## How to use

(planned)

1. Open the tool window: `Window > Project-wide VRC Constraints Converter`
2. Click the `Find Constraint assets` button to find all assets in your project that have Unity constraints.
   This may take a while depending on the size of your project.
3. See list of files that will be converted.
   If you want, you can manually exclude some files from the conversion process.
4. Click the `Convert` button to convert assets to VRC Constraints.
   This process is in-place, so it will overwrite the original assets.
   **!!!Backup your project before clicking Convert Button!!!**
5. Done!

## License

MIT License

## Other planned / ideas for future development

- Back conversion (VRC Constraints to Unity Constraints)
  - This will be useful for porting your content to other platforms.
  - This back conversion should not be project-wide, should be applied for specific assets or avatar.
- Auto convertion with NDMF or `IVRCSDKPreprocessAvatarCallback`
  - Automatically convert constraints when building your avatar.
