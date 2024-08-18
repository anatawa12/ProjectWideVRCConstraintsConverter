# Project-wide VRC Constraints Converter

This tool is used to convert Unity constraints to VRC Constraints for all assets in your project.\
プロジェクト内のすべてのアセットに含まれるUnity製ConstraintをVRCConstraintに変換するツールです。

In the tool window, you can exclude some folders or assets from the conversion process.\
ツールのウィンドウでは、変換処理から除外したいフォルダやアセットを指定することができます。

## Installation
【導入方法】

1. Add anatawa12's vpm repository to your VCC, ALCOM, or other VPM client. [this][add-repo] is the link to open VCC or ALCOM.\
VCCやALCOMなどのVPMクライアントにanatawa12のVPMリポジトリを追加します。\
[このリンク][add-repo]からVCCやALCOMに追加することができます。
2. Install `Project-Wide VRC Constraints Converter` from the VCC, ALCOM, or other VPM client.\
VCCやALCOMなどのVPMクライアントから、プロジェクトに`Project-Wide VRC Constraints Converter`をインストールします。

[add-repo]: https://vpm.anatawa12.com/add-repo

## How to use
【使い方】

1. Open the tool window: `Tools > Project-wide VRC Constraints Converter`\
Unityの画面上部メニューの`Tools`→`Project-wide VRC Constraints Converter`からツールのウィンドウを開きます。
2. Click the `Search files to convert` button to find all assets in your project that have Unity constraints.
   This may take a while depending on the size of your project.\
`Search files to convert`ボタンをクリックすると、プロジェクト内のすべてのアセットからUnity製Constraintが使用されているものを探します。
プロジェクトが大きいと処理に時間が掛かることがあります。
3. See list of files that will be converted.
   If you want, you can manually exclude some files from the conversion process.\
変換対象の一覧を見ることができます。
必要であれば、手動で一部のファイルを変換対象から除外することができます。
5. Click the `Convert` button to convert assets to VRC Constraints.
   This process is in-place, so it will overwrite the original assets.
   **!!!Backup your project before clicking Convert Button!!!**\
`Convert`ボタンを押すことでVRCConstraintへの変換を行います。\
この処理は元のアセットを上書きするため、**変換処理を行う前に必ずプロジェクトをバックアップしてください!!!**
7. Done!\
完了です!

## License
【ライセンス】

MIT License

## Other planned / ideas for future development
【実装を検討している機能】

- Back conversion (VRC Constraints to Unity Constraints)\
逆変換 (VRCConstraintからUnity製Constraint)
  - This will be useful for porting your content to other platforms.
  - This back conversion should not be project-wide, should be applied for specific assets or avatar.
- Auto convertion with NDMF or `IVRCSDKPreprocessAvatarCallback`\
NDMFや`IVRCSDKPreprocessAvatarCallback`を使用した自動変換
  - Automatically convert constraints when building your avatar.

## Notable Changes

### 0.1.0

Initial release

### 0.1.1

Fixed error with broken prefab assets

### 0.1.2

Fixed some assets are not proceed correctly
