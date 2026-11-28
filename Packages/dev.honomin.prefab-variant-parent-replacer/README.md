# Prefab Variant Parent Replacer

Prefab Variantの親（Variant Parent）を一括で差し替えるUnityエディタ拡張です。新旧の親Prefab間でFileIDを照合し、差し替え後のシーンや他アセットからの外部参照も修復します。

作者：**honomin3** ／ 配布元：**honomin3's Packages** ／ パッケージID：`dev.honomin.prefab-variant-parent-replacer`

> [!CAUTION]
> **免責事項 (Disclaimer) - ご利用前に必ずお読みください**
> 本ツールは非破壊ツールではなく、**Prefabの内部データ（YAML）を直接書き換える「破壊的変更」を行うツール**です。
> * **無保証 (AS IS)**: 本ツールは現状有姿のまま提供され、一切の動作保証を行いません。
> * **自己責任**: 本ツールの使用によって生じたデータの破損、アバターの消失、その他いかなる損害についても、作者（honomin3）は一切の責任を負いません。
> * **サポート対象外**: 個別環境での不具合調査、バグ修正、機能追加のご要望、および使い方に関するサポート対応は**一切行いません**。
>
> 上記に同意いただける方のみ、ご自身の責任において自由にご利用・改造ください。利用前に必ずプロジェクトのバックアップを取得することを強く推奨します。

## 動作環境

- パッケージの最小Unityバージョン：**2022.3**。
- Project Settingsの **Editor → Asset Serialization → Mode** を **Force Text** に設定したプロジェクト。
- Unity Editor専用です。VRChat SDK・Modular Avatar（MA）・NDMFへの依存はありません。

VRChat向けに使用する場合は、[VRChat公式の対応Unityバージョン](https://creators.vrchat.com/sdk/upgrade/current-unity-version/)も確認してください。すべてのUnityバージョン・アバター・外部ツールとの組み合わせを検証済みとはしていません。

## 導入方法

### VCCから導入する

既存のプロジェクトは変更前にバックアップを作成し、導入先のUnityを閉じてから進めてください。従来のスクリプト版を使用している場合は、後述の移行手順も確認してください。

1. VRChat Creator Companion（VCC）で導入先のプロジェクトを用意します。
2. VCCの **Settings → Packages → Add Repository** を開き、次のURLを入力して **Add** を押します。

   ```text
   https://raw.githubusercontent.com/honomin3/Packages/main/index.json
   ```

3. 配布元が **honomin3's Packages**、パッケージが **Prefab Variant Parent Replacer** であることを確認し、**I Understand, Add Repository** を押します。配布元を登録済みの場合は再登録不要です。
4. **Projects** から導入先の **Manage Project** を開きます。**Prefab Variant Parent Replacer** の右側の **＋**、または導入したいバージョンを選択してインストールします。
5. Unityを開き、読み込み・コンパイルが完了するまで待ちます。Projectウィンドウの **Packages** にPrefab Variant Parent Replacerが表示されれば導入完了です。

一覧に表示されない場合は、VCCのリポジトリ一覧や **Selected Repos** で **honomin3's Packages** が有効か確認してください。画面やボタン名は利用中の版で異なる場合があります。[配布元の追加](https://vcc.docs.vrchat.com/guides/community-repositories/)と[プロジェクトへの追加](https://vcc.docs.vrchat.com/vpm/)はVCC公式説明も参照してください。

### ALCOMを使う場合

ALCOMでも同じVPMリポジトリURLを使えます。**パッケージ管理 → VPMリポジトリを追加** から上記のURLを登録し、導入先プロジェクトの管理画面で **Prefab Variant Parent Replacer** を追加して変更を適用します。MA／NDMFを追加する必要はありません。

### 配布ファイルを直接使う場合

[GitHub Releases](https://github.com/honomin3/PrefabVariantParentReplacer/releases)から対象バージョンのファイルを取得します。通常の導入・更新にはVCC／ALCOMを使用し、同じパッケージを複数の方法で重ねて導入しないでください。

- **UPM用 `.tgz`**：Unityの **Window → Package Manager → ＋ → Add package from tarball…** で `.tgz` を選択します。展開は不要です。
- **VPM用 `.zip`**：ZIPを展開し、`package.json` が入ったフォルダーをVCCの **Settings → Packages → User Packages → Add** から登録します。その後、導入先プロジェクトのパッケージ一覧から追加します。ZIPをAssetsへそのまま入れる方式ではありません。[User Packagesの公式説明](https://vcc.docs.vrchat.com/vpm/packages/)も参照してください。

ソースから確認する場合は、このリポジトリを取得し、`Packages/dev.honomin.prefab-variant-parent-replacer` をUser Packagesに登録できます。UPMでは **Add package from disk…** で同フォルダーの `package.json` を指定します。

### 従来のAssets配置から移行する

1. プロジェクトをバックアップし、Unityを閉じます。
2. Assets内に手動配置した `PrefabVariantParentReplacer.cs` を取り除きます。別のEditorフォルダーへ移動・複製している場合も確認してください。バックアップはプロジェクトのAssets外へ保管します。
3. VCC／ALCOM、または配布ファイルから本パッケージを導入します。
4. Unityを開き、同じクラスの重複定義エラーが出ないことと、従来のメニューからツールを開けることを確認します。

VPM版は、従来の標準配置 `Assets/honomin3/PrefabVariantParentReplacer/Editor/PrefabVariantParentReplacer.cs` を `legacyFiles` に登録しています。VPMによる移行時はこのファイルが削除対象になります。別の場所へ配置したコピーや、UPMによる導入では手動で取り除いてください。既存のPrefabやシーンを移動する必要はありません。

## 基本的な使い方

アバター（A）から改変ベース（B）を作り、そこから衣装差分（C）を作った後、改変ベースを作り直した（D）際に、衣装差分（C）の親をBからDへ差し替える用途を想定しています。

実行前にプロジェクトまたは対象Prefabと参照元アセットのバックアップを取得してください。

1. Unity上部の **Tools → honomin3 → Prefab Variant Parent Replacer** からウィンドウを開きます。
2. **旧Variant Parent** に従来の親Prefab、**新Variant Parent** に差し替え先の親Prefabを指定します。
3. **対象Prefab（複数可）** に、親を差し替えたいPrefab Variantを登録します。
4. 必要に応じて **Humanoidボーンによるフォールバック照合**、**一意なGameObject名によるフォールバック照合**、**シーン・他アセットからの外部参照切れを自動修復** を設定します。
5. **1. 事前解析（未解決FileIDの確認・手動マッピング準備）** を実行し、自動照合できなかった項目を確認します。必要な項目に手動で対応オブジェクトを指定します。
6. 必要に応じて **書き換え前に .bak バックアップを作成** を有効にします。このオプションの初期値はオフです。
7. **Variant Parentを差し替える（GUIDとFileIDを自動修正）** を実行します。
8. 差し替えたPrefabとシーンを開き、Missingや外部参照、アバターの状態を確認します。

## 主な機能と制限

### FileIDの照合と手動マッピング

新旧の親で追加された服やボーンなどはFileIDが異なるため、GUIDだけを置き換えるとMissingになる場合があります。本ツールは新旧の親の階層とコンポーネントからFileIDの変換表を作成し、GUIDと同時に参照先FileIDを修正します。

階層パス一致に加え、Humanoidボーン一致・一意なGameObject名一致によるフォールバック照合と、事前解析結果からの手動マッピングを利用できます。

> [!WARNING]
> 自動照合・手動マッピングでも対応付けできなかった参照はMissingになる可能性があります。差し替え後に確認し、必要な参照を手動で再設定してください。

### シーン・他アセットからの外部参照修復

親差し替え後の再インポートで対象PrefabのローカルFileIDが変わると、シーンや他アセットからの参照が外れることがあります。外部参照修復を有効にすると、変更前後のFileIDを追跡し、Assets内の `.unity`、`.prefab`、`.asset`、`.controller` などの参照を書き換えます。

- **Force Text** のテキストアセットが対象です。
- バイナリ形式やJSONなどの独自形式で保存された参照は修復対象外です。
- 構造変更により対応付けできない要素は修復対象外です。
- `.bak` 作成を有効にした場合は、書き換えるPrefab・参照元ファイルの隣にバックアップを作成します。プロジェクト全体のバックアップも別途用意してください。

### 対象外Prefabのスキップ

指定した旧Variant ParentのGUIDがファイル内に存在しないPrefabは、警告を出してスキップします。事前解析の結果と実行後のログを確認してください。

## 更新・サポート方針

VCC／ALCOMで導入した場合は、同じプロジェクトの管理画面から本パッケージのバージョンを更新してください。更新内容は[CHANGELOG](CHANGELOG.md)、配布物は[Releases](https://github.com/honomin3/PrefabVariantParentReplacer/releases)で確認できます。

個別環境での調査・修正・機能追加・使い方のサポートは行いません。冒頭の免責事項を確認のうえ、ご自身の責任で利用・改造してください。

## ライセンス

[MIT License](LICENSE)。Unity・VRChat・VCC・ALCOMの公式製品ではありません。
