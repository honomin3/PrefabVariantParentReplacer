using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

public class PrefabVariantParentReplacer : EditorWindow
{
    public GameObject oldBasePrefab;
    public GameObject newBasePrefab;
    public List<GameObject> targetPrefabs = new List<GameObject>();

    private SerializedObject so;
    private SerializedProperty propOldBase;
    private SerializedProperty propNewBase;
    private SerializedProperty propTargets;
    private Vector2 scrollPosition;

    [MenuItem("Tools/Prefab Variant Parent Replacer")]
    public static void ShowWindow()
    {
        GetWindow<PrefabVariantParentReplacer>("Prefab Variant Parent Replacer");
    }

    private void OnEnable()
    {
        so = new SerializedObject(this);
        propOldBase = so.FindProperty("oldBasePrefab");
        propNewBase = so.FindProperty("newBasePrefab");
        propTargets = so.FindProperty("targetPrefabs");
    }

    private void OnGUI()
    {
        so.Update();

        EditorGUILayout.LabelField("Prefab Variant Parent一括差し替えツール", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "対象となるPrefab VariantのVariant Parentを一括で差し替えます。\n\n" +
            "【想定用途】\n" +
            "アバター(A)から改変ベース(B)を作り、そこから衣装差分(C)を作った後、改変ベースを作り直した(D)際に、衣装差分(C)の親をBからDに差し替える用途に最適です。\n\n" +
            "【各項目の配置について】\n" +
            "・旧Variant Parent: これまで親だった古いVariant ParentのPrefabを指定します。\n" +
            "・新Variant Parent: 新しく親にしたい新しいVariant ParentのPrefabを指定します。\n" +
            "・対象Prefab (複数可): Variant Parentを差し替えたい衣装差分などのPrefab Variantを指定します。", 
            MessageType.Info);

        EditorGUILayout.HelpBox(
            "【⚠ 重要な注意点（必ずお読みください）】\n" +
            "・本ツールは非破壊ツールではなく「破壊的変更ツール」です。実行前に必ずバックアップを取ってください。\n" +
            "・置換後、内部ID（FileID）が振り直されるため、対象Prefabを配置しているSceneや、対象Prefabを参照している外部ツール等でMissing（参照切れ）が発生します。置換後は手動での再配置・再登録が必要です。\n" +
            "・旧と新で内部構造が大きく異なる場合、対応付けができず一部のオーバーライド設定が外れる可能性があります。",
            MessageType.Warning);

        EditorGUILayout.Space();

        EditorGUILayout.PropertyField(propOldBase, new GUIContent("旧Variant Parent"));
        EditorGUILayout.PropertyField(propNewBase, new GUIContent("新Variant Parent"));

        EditorGUILayout.Space();

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        EditorGUILayout.PropertyField(propTargets, new GUIContent("対象Prefab (複数可)"), true);
        EditorGUILayout.EndScrollView();

        so.ApplyModifiedProperties();

        EditorGUILayout.Space();

        if (GUILayout.Button("Variant Parentを差し替える (GUIDとFileIDを自動修正)", GUILayout.Height(30)))
        {
            ExecuteRebase();
        }
    }

    private void ExecuteRebase()
    {
        if (oldBasePrefab == null || newBasePrefab == null || targetPrefabs.Count == 0)
        {
            EditorUtility.DisplayDialog("エラー", "旧Variant Parent、新Variant Parent、および対象Prefabを少なくとも1つ指定してください。", "OK");
            return;
        }

        string oldPath = AssetDatabase.GetAssetPath(oldBasePrefab);
        string newPath = AssetDatabase.GetAssetPath(newBasePrefab);

        string oldGuid = AssetDatabase.AssetPathToGUID(oldPath);
        string newGuid = AssetDatabase.AssetPathToGUID(newPath);

        if (string.IsNullOrEmpty(oldGuid) || string.IsNullOrEmpty(newGuid))
        {
            EditorUtility.DisplayDialog("エラー", "指定されたVariant ParentのGUIDを取得できませんでした。", "OK");
            return;
        }

        // --- FileID マッピングの作成 ---
        EditorUtility.DisplayProgressBar("Variant Parent差し替え処理", "FileIDの変換表を作成中...", 0.1f);
        Dictionary<long, long> fileIdMapping = CreateFileIDMapping(oldBasePrefab, newBasePrefab);

        int successCount = 0;
        int total = targetPrefabs.Count;

        for (int i = 0; i < total; i++)
        {
            GameObject target = targetPrefabs[i];
            if (target == null) continue;

            EditorUtility.DisplayProgressBar("Variant Parent差し替え処理", $"{target.name} を書き換え中...", (float)(i + 1) / total);

            string targetPath = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrEmpty(targetPath)) continue;

            string fullPath = Path.Combine(Directory.GetCurrentDirectory(), targetPath);

            if (!File.Exists(fullPath))
            {
                Debug.LogError($"File not found: {fullPath}");
                continue;
            }

            string content = File.ReadAllText(fullPath);

            if (!content.Contains(oldGuid))
            {
                Debug.LogWarning($"Old GUID ({oldGuid}) not found in target {target.name}. Skipping.");
                continue;
            }

            // 1. 正規表現で旧GUIDを参照している箇所のFileIDを変換表に従って置換する
            // 書式: fileID: 1234567, guid: abcdef...
            string pattern = $@"fileID:\s*(?<fid>-?\d+),\s*guid:\s*{oldGuid}";
            int replacedFileIdCount = 0;
            
            content = Regex.Replace(content, pattern, match =>
            {
                if (long.TryParse(match.Groups["fid"].Value, out long fid))
                {
                    if (fileIdMapping.TryGetValue(fid, out long newFid))
                    {
                        replacedFileIdCount++;
                        // 変換表にあった場合は新しいFileIDに差し替える
                        return $"fileID: {newFid}, guid: {oldGuid}";
                    }
                }
                // 変換表になかった場合はFileIDはそのまま
                return match.Value;
            });

            // 2. 最後にGUID自体を一括置換する
            content = content.Replace(oldGuid, newGuid);

            File.WriteAllText(fullPath, content);
            successCount++;
            Debug.Log($"Successfully rebased: {target.name} (Replaced GUID, and fixed {replacedFileIdCount} FileID(s))");
        }

        EditorUtility.ClearProgressBar();

        if (successCount > 0)
        {
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("完了", $"{successCount} 個のPrefabのVariant Parent差し替えが完了しました。\n\n旧Variant Parent: {oldBasePrefab.name}\n新Variant Parent: {newBasePrefab.name}", "OK");
        }
        else
        {
            EditorUtility.DisplayDialog("結果", "書き換えられたPrefabはありませんでした。詳細はコンソールの警告をご確認ください。", "OK");
        }
    }

    private Dictionary<long, long> CreateFileIDMapping(GameObject oldBase, GameObject newBase)
    {
        Dictionary<long, long> mapping = new Dictionary<long, long>();

        string oldPath = AssetDatabase.GetAssetPath(oldBasePrefab);
        string newPath = AssetDatabase.GetAssetPath(newBasePrefab);

        // Prefab内の全アセット（GameObject, Componentなど）を取得
        Object[] oldAssets = AssetDatabase.LoadAllAssetsAtPath(oldPath);
        Object[] newAssets = AssetDatabase.LoadAllAssetsAtPath(newPath);

        Dictionary<string, long> newAssetLookup = new Dictionary<string, long>();

        // 新しいPrefabの解析
        foreach (var newAsset in newAssets)
        {
            if (newAsset == null) continue;
            string key = GetAssetKey(newAsset, newBase.transform);
            if (string.IsNullOrEmpty(key)) continue;

            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(newAsset, out string guid, out long fileId))
            {
                if (!newAssetLookup.ContainsKey(key))
                {
                    newAssetLookup[key] = fileId;
                }
            }
        }

        // 古いPrefabの解析と比較
        foreach (var oldAsset in oldAssets)
        {
            if (oldAsset == null) continue;
            string key = GetAssetKey(oldAsset, oldBase.transform);
            if (string.IsNullOrEmpty(key)) continue;

            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(oldAsset, out string oldGuid, out long oldFileId))
            {
                if (newAssetLookup.TryGetValue(key, out long newFileId))
                {
                    // FileIDが異なる場合はマッピングに登録
                    if (oldFileId != newFileId)
                    {
                        mapping[oldFileId] = newFileId;
                    }
                }
            }
        }

        return mapping;
    }

    private string GetAssetKey(Object obj, Transform root)
    {
        if (obj is GameObject go)
        {
            return AnimationUtility.CalculateTransformPath(go.transform, root) + "_GameObject";
        }
        else if (obj is Component comp)
        {
            string path = AnimationUtility.CalculateTransformPath(comp.transform, root);
            string typeName = comp.GetType().Name;
            
            // 同一GameObject上の同一コンポーネントを区別するためインデックスを取得
            Component[] comps = comp.gameObject.GetComponents(comp.GetType());
            int index = System.Array.IndexOf(comps, comp);
            
            return $"{path}_{typeName}_{index}";
        }
        return null;
    }
}
