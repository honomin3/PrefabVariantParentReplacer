// Based on PrefabVariantParentReplacer by honomin3
// https://github.com/honomin3/PrefabVariantParentReplacer
// MIT License - Copyright (c) 2026 honomin3
//
// Modified to support:
// 1. Automatic repair of external references (Scenes, Prefabs, ScriptableObjects, etc.)
//    after target Prefab Variants are re-imported with new local FileIDs.
// 2. Improved parent-to-parent FileID matching (exact path, Humanoid bone fallback,
//    unique GameObject name fallback, and manual mapping UI for unresolved references).
// 3. Headless execution/analysis API for Unity Test Framework (EditMode tests).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PrefabVariantParentReplacer : EditorWindow
{
    public GameObject oldBasePrefab;
    public GameObject newBasePrefab;
    public List<GameObject> targetPrefabs = new List<GameObject>();

    [Header("オプション")]
    public bool repairExternalReferences = true;
    public bool useHumanoidBoneFallback = true;
    public bool useUniqueNameFallback = true;
    public bool createBackupFiles = false;

    private SerializedObject so;
    private SerializedProperty propOldBase;
    private SerializedProperty propNewBase;
    private SerializedProperty propTargets;
    private SerializedProperty propRepairExternal;
    private SerializedProperty propHumanoidFallback;
    private SerializedProperty propUniqueNameFallback;
    private SerializedProperty propCreateBackup;

    private Vector2 mainScrollPosition;
    private Vector2 unresolvedScrollPosition;

    // 事前解析結果
    private bool hasAnalyzed = false;
    private string analyzedSignature = "";
    private AnalysisResult lastAnalysis = null;

    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    // 外部参照の自動修復で走査するテキスト系アセット拡張子
    private static readonly HashSet<string> ExternalRepairExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".unity",
        ".prefab",
        ".asset",
        ".controller",
        ".overrideController",
        ".anim",
        ".mat",
        ".playable",
        ".mask",
        ".mixer",
        ".signal",
        ".preset"
    };

    #region Public API Models (GUI / Test Runner 共通)

    public class RebaseOptions
    {
        public GameObject OldBasePrefab;
        public GameObject NewBasePrefab;
        public List<GameObject> TargetPrefabs = new List<GameObject>();
        public bool RepairExternalReferences = true;
        public bool UseHumanoidBoneFallback = true;
        public bool UseUniqueNameFallback = true;
        public bool CreateBackupFiles = false;
        public Dictionary<long, UnityEngine.Object> ManualMappings = new Dictionary<long, UnityEngine.Object>();
        /// <summary>
        /// 外部参照修復のスキャン対象ルート（プロジェクト相対パス、例: "Assets"）。
        /// テスト時に一時フォルダへ限定して高速化するために使用できます。
        /// </summary>
        public string ExternalRepairRootAssetFolder = "Assets";
        public bool ShowUI = true;
        public bool ReloadModifiedOpenScenes = true;
    }

    public class UnresolvedEntry
    {
        public long OldFileId;
        public string DisplayLabel;
        public string TransformPath;
        public string TypeFullName;
        public string TypeShortName;
        public int ComponentIndex;
        public bool IsGameObject;
        public UnityEngine.Object ManualNewObject;
    }

    public class AnalysisResult
    {
        public bool Succeeded;
        public string ErrorMessage;
        public int ExactMatched;
        public int HumanoidMatched;
        public int UniqueNameMatched;
        public int ValidTargetCount;
        public int SkippedTargetCount;
        public List<UnresolvedEntry> UnresolvedEntries = new List<UnresolvedEntry>();
    }

    public class RebaseResult
    {
        public bool Succeeded;
        public string ErrorMessage;
        public int RebasedPrefabCount;
        public int SkippedPrefabCount;
        public int FixedParentFileIdCount;
        public int UnresolvedParentRefCount;
        public int RepairedExternalFileCount;
        public int RepairedExternalRefCount;
        public List<string> ModifiedScenePaths = new List<string>();
    }

    #endregion

    #region Internal Models

    private class AssetRecord
    {
        public UnityEngine.Object Asset;
        public long LocalFileId;
        public bool IsGameObject;
        public string ExactKey;
        public string TransformPath;
        public string ObjectName;
        public string TypeFullName;
        public string TypeShortName;
        public int ComponentIndex;
        public HumanBodyBones HumanoidBone = HumanBodyBones.LastBone;
    }

    private class ParentMappingResult
    {
        public Dictionary<long, long> FileIdMap = new Dictionary<long, long>();
        public Dictionary<string, string> TransformPathMap = new Dictionary<string, string>();
        public Dictionary<long, AssetRecord> OldRecordsByFileId = new Dictionary<long, AssetRecord>();
        public Dictionary<long, AssetRecord> NewRecordsByFileId = new Dictionary<long, AssetRecord>();
        public int ExactMatchCount;
        public int HumanoidMatchCount;
        public int UniqueNameMatchCount;
        public int ManualMatchCount;
    }

    private class TargetSnapshot
    {
        public string AssetPath;
        public string FullPath;
        public string TargetGuid;
        public string TargetName;
        public Dictionary<long, AssetRecord> BeforeRecords;
    }

    private class OpenSceneState
    {
        public string Path;
        public bool IsLoaded;
        public bool IsActive;
    }

    #endregion

    [MenuItem("Tools/honomin3/Prefab Variant Parent Replacer")]
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
        propRepairExternal = so.FindProperty("repairExternalReferences");
        propHumanoidFallback = so.FindProperty("useHumanoidBoneFallback");
        propUniqueNameFallback = so.FindProperty("useUniqueNameFallback");
        propCreateBackup = so.FindProperty("createBackupFiles");
    }

    private void OnGUI()
    {
        so.Update();

        mainScrollPosition = EditorGUILayout.BeginScrollView(mainScrollPosition);

        EditorGUILayout.LabelField("Prefab Variant Parent 一括差し替えツール（参照修復対応版）", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "対象となるPrefab Variantの親（Variant Parent）を一括差し替えします。\n" +
            "・親差し替えに伴う内部FileIDの自動照合（階層パス一致 / Humanoidボーン一致 / 一意名一致 / 手動マッピング）\n" +
            "・差し替え後に振り直される対象PrefabのFileIDを追跡し、シーンや他アセットからの参照切れを自動修復",
            MessageType.Info);

        if (EditorSettings.serializationMode != SerializationMode.ForceText)
        {
            EditorGUILayout.HelpBox(
                "Project Settings の Asset Serialization Mode が 'Force Text' ではありません。\n" +
                "YAMLテキスト書き換えを行うため、事前に Force Text へ変更することを強く推奨します。",
                MessageType.Error);
        }

        EditorGUILayout.HelpBox(
            "【注意】本ツールは .prefab や参照元アセットのYAMLを直接書き換えます。実行前に必ずバックアップ（またはGitコミット）を取ってください。",
            MessageType.Warning);

        EditorGUILayout.Space();

        EditorGUILayout.PropertyField(propOldBase, new GUIContent("旧Variant Parent"));
        EditorGUILayout.PropertyField(propNewBase, new GUIContent("新Variant Parent"));

        EditorGUILayout.Space();
        EditorGUILayout.PropertyField(propTargets, new GUIContent("対象Prefab（複数可）"), true);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("修復・照合オプション", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(propRepairExternal, new GUIContent("シーン・他アセットからの外部参照切れを自動修復"));
        EditorGUILayout.PropertyField(propHumanoidFallback, new GUIContent("Humanoidボーンによるフォールバック照合"));
        EditorGUILayout.PropertyField(propUniqueNameFallback, new GUIContent("一意なGameObject名によるフォールバック照合"));
        EditorGUILayout.PropertyField(propCreateBackup, new GUIContent("書き換え前に .bak バックアップを作成"));

        so.ApplyModifiedProperties();

        string currentSig = BuildInputSignature();
        if (hasAnalyzed && currentSig != analyzedSignature)
        {
            hasAnalyzed = false;
            lastAnalysis = null;
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("1. 事前解析（未解決FileIDの確認・手動マッピング準備）", GUILayout.Height(28)))
        {
            RunPreAnalysisFromGUI();
        }

        if (hasAnalyzed && lastAnalysis != null)
        {
            DrawAnalysisResultUI();
        }

        EditorGUILayout.Space();

        GUI.backgroundColor = new Color(0.6f, 0.9f, 0.7f);
        if (GUILayout.Button("2. Variant Parentを差し替えて参照を修復する", GUILayout.Height(34)))
        {
            ExecuteRebaseFromGUI();
        }
        GUI.backgroundColor = Color.white;

        EditorGUILayout.EndScrollView();
    }

    private void DrawAnalysisResultUI()
    {
        EditorGUILayout.Space();
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("事前解析結果", EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"有効な対象Prefab: {lastAnalysis.ValidTargetCount} 件 / スキップ予定: {lastAnalysis.SkippedTargetCount} 件");
        EditorGUILayout.LabelField(
            $"親FileID自動照合: 完全一致 {lastAnalysis.ExactMatched} 件 / Humanoid {lastAnalysis.HumanoidMatched} 件 / 一意名 {lastAnalysis.UniqueNameMatched} 件");

        if (lastAnalysis.UnresolvedEntries.Count == 0)
        {
            EditorGUILayout.HelpBox("対象Prefab内で参照されている旧親FileIDはすべて自動解決できました。", MessageType.Info);
        }
        else
        {
            EditorGUILayout.HelpBox(
                $"対象Prefab内で参照されているうち、{lastAnalysis.UnresolvedEntries.Count} 件の旧親FileIDが自動照合できませんでした。\n" +
                "必要に応じて、新Variant Parent側の対応するGameObjectまたはComponentを以下に指定してください。\n" +
                "（※Component項目にGameObjectをドロップすると、同じ型のComponentを自動抽出します）",
                MessageType.Warning);

            unresolvedScrollPosition = EditorGUILayout.BeginScrollView(
                unresolvedScrollPosition,
                GUILayout.MaxHeight(220));

            for (int i = 0; i < lastAnalysis.UnresolvedEntries.Count; i++)
            {
                UnresolvedEntry entry = lastAnalysis.UnresolvedEntries[i];
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    new GUIContent(entry.DisplayLabel, $"Old FileID: {entry.OldFileId}\nPath: {entry.TransformPath}"),
                    GUILayout.MinWidth(220));

                UnityEngine.Object picked = EditorGUILayout.ObjectField(entry.ManualNewObject, typeof(UnityEngine.Object), false);
                if (picked != entry.ManualNewObject)
                {
                    if (picked == null)
                    {
                        entry.ManualNewObject = null;
                    }
                    else if (!IsAssetInsidePrefab(picked, newBasePrefab))
                    {
                        EditorUtility.DisplayDialog(
                            "指定エラー",
                            "新Variant Parent 内の GameObject または Component を指定してください。",
                            "OK");
                    }
                    else
                    {
                        entry.ManualNewObject = ResolvePickedManualObject(entry, picked, true);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }
        EditorGUILayout.EndVertical();
    }

    private void RunPreAnalysisFromGUI()
    {
        Dictionary<long, UnityEngine.Object> previousManual = new Dictionary<long, UnityEngine.Object>();
        if (lastAnalysis != null)
        {
            foreach (UnresolvedEntry prev in lastAnalysis.UnresolvedEntries)
            {
                if (prev.ManualNewObject != null)
                {
                    previousManual[prev.OldFileId] = prev.ManualNewObject;
                }
            }
        }

        RebaseOptions options = BuildOptionsFromFields(true);
        AnalysisResult result = AnalyzeCore(options);
        if (!result.Succeeded)
        {
            EditorUtility.DisplayDialog("エラー", result.ErrorMessage, "OK");
            return;
        }

        foreach (UnresolvedEntry entry in result.UnresolvedEntries)
        {
            if (previousManual.TryGetValue(entry.OldFileId, out UnityEngine.Object prevObj))
            {
                entry.ManualNewObject = prevObj;
            }
        }

        lastAnalysis = result;
        analyzedSignature = BuildInputSignature();
        hasAnalyzed = true;
    }

    private void ExecuteRebaseFromGUI()
    {
        if (repairExternalReferences)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }
        }

        RebaseOptions options = BuildOptionsFromFields(true);
        if (hasAnalyzed && lastAnalysis != null && BuildInputSignature() == analyzedSignature)
        {
            foreach (UnresolvedEntry entry in lastAnalysis.UnresolvedEntries)
            {
                if (entry.ManualNewObject != null)
                {
                    options.ManualMappings[entry.OldFileId] = entry.ManualNewObject;
                }
            }
        }

        RebaseResult result = ExecuteRebaseCore(options);
        if (!result.Succeeded)
        {
            EditorUtility.DisplayDialog("エラー", result.ErrorMessage, "OK");
            return;
        }

        hasAnalyzed = false;
        lastAnalysis = null;

        if (result.RebasedPrefabCount > 0)
        {
            string summaryMsg =
                $"{result.RebasedPrefabCount} 個のPrefabのVariant Parent差し替えが完了しました。\n\n" +
                $"旧Variant Parent: {oldBasePrefab.name}\n" +
                $"新Variant Parent: {newBasePrefab.name}\n" +
                $"親FileID修正数: {result.FixedParentFileIdCount} 箇所";

            if (repairExternalReferences)
            {
                summaryMsg += $"\n外部参照修復: {result.RepairedExternalFileCount} ファイル / {result.RepairedExternalRefCount} 箇所";
            }

            EditorUtility.DisplayDialog("完了", summaryMsg, "OK");
        }
        else
        {
            EditorUtility.DisplayDialog("結果", "書き換え対象となるPrefabがありませんでした。コンソールのログを確認してください。", "OK");
        }
    }

    private RebaseOptions BuildOptionsFromFields(bool showUI)
    {
        return new RebaseOptions
        {
            OldBasePrefab = oldBasePrefab,
            NewBasePrefab = newBasePrefab,
            TargetPrefabs = new List<GameObject>(targetPrefabs),
            RepairExternalReferences = repairExternalReferences,
            UseHumanoidBoneFallback = useHumanoidBoneFallback,
            UseUniqueNameFallback = useUniqueNameFallback,
            CreateBackupFiles = createBackupFiles,
            ExternalRepairRootAssetFolder = "Assets",
            ShowUI = showUI,
            ReloadModifiedOpenScenes = true
        };
    }

    private string BuildInputSignature()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(oldBasePrefab != null ? oldBasePrefab.GetInstanceID().ToString() : "null");
        sb.Append("|");
        sb.Append(newBasePrefab != null ? newBasePrefab.GetInstanceID().ToString() : "null");
        sb.Append("|");
        sb.Append(useHumanoidBoneFallback);
        sb.Append("|");
        sb.Append(useUniqueNameFallback);
        sb.Append("|");
        for (int i = 0; i < targetPrefabs.Count; i++)
        {
            sb.Append(targetPrefabs[i] != null ? targetPrefabs[i].GetInstanceID().ToString() : "null");
            sb.Append(",");
        }
        return sb.ToString();
    }

    #region Core Headless API

    public static AnalysisResult AnalyzeCore(RebaseOptions options)
    {
        AnalysisResult result = new AnalysisResult();

        if (!ValidateOptions(options, out string oldGuid, out string newGuid, out List<GameObject> targets, out string error))
        {
            result.Succeeded = false;
            result.ErrorMessage = error;
            return result;
        }

        try
        {
            if (options.ShowUI)
                EditorUtility.DisplayProgressBar("事前解析", "新旧Variant Parentを照合中...", 0.3f);

            ParentMappingResult mappingResult = CreateParentFileIdMapping(
                options.OldBasePrefab,
                options.NewBasePrefab,
                options.UseHumanoidBoneFallback,
                options.UseUniqueNameFallback,
                null);

            HashSet<long> referencedOldFileIds = new HashSet<long>();
            int validCount = 0;
            int skippedCount = 0;
            Regex refRegex = new Regex($@"fileID:\s*(?<fid>-?\d+),\s*guid:\s*{Regex.Escape(oldGuid)}");

            for (int i = 0; i < targets.Count; i++)
            {
                if (options.ShowUI)
                    EditorUtility.DisplayProgressBar("事前解析", $"{targets[i].name} の参照を確認中...", 0.3f + 0.6f * (i + 1) / targets.Count);

                string targetPath = AssetDatabase.GetAssetPath(targets[i]);
                string fullPath = ToFullPath(targetPath);
                if (!File.Exists(fullPath))
                {
                    skippedCount++;
                    continue;
                }

                string content = File.ReadAllText(fullPath, Utf8NoBom);
                if (!content.Contains(oldGuid))
                {
                    skippedCount++;
                    continue;
                }

                validCount++;
                MatchCollection matches = refRegex.Matches(content);
                foreach (Match m in matches)
                {
                    if (long.TryParse(m.Groups["fid"].Value, out long fid))
                    {
                        referencedOldFileIds.Add(fid);
                    }
                }
            }

            foreach (long fid in referencedOldFileIds.OrderBy(x => x))
            {
                if (mappingResult.FileIdMap.ContainsKey(fid)) continue;

                if (mappingResult.OldRecordsByFileId.TryGetValue(fid, out AssetRecord oldRec))
                {
                    result.UnresolvedEntries.Add(new UnresolvedEntry
                    {
                        OldFileId = fid,
                        TransformPath = string.IsNullOrEmpty(oldRec.TransformPath) ? "(Root)" : oldRec.TransformPath,
                        TypeFullName = oldRec.TypeFullName,
                        TypeShortName = oldRec.TypeShortName,
                        ComponentIndex = oldRec.ComponentIndex,
                        IsGameObject = oldRec.IsGameObject,
                        DisplayLabel = oldRec.IsGameObject
                            ? $"[GO] {(string.IsNullOrEmpty(oldRec.TransformPath) ? oldRec.ObjectName : oldRec.TransformPath)}"
                            : $"[{oldRec.TypeShortName}] {(string.IsNullOrEmpty(oldRec.TransformPath) ? oldRec.ObjectName : oldRec.TransformPath)}"
                    });
                }
                else
                {
                    result.UnresolvedEntries.Add(new UnresolvedEntry
                    {
                        OldFileId = fid,
                        TransformPath = "(Unknown / Missing in Old Parent)",
                        TypeFullName = "",
                        TypeShortName = "Object",
                        ComponentIndex = 0,
                        IsGameObject = false,
                        DisplayLabel = $"[Unknown] fileID: {fid}"
                    });
                }
            }

            result.ExactMatched = mappingResult.ExactMatchCount;
            result.HumanoidMatched = mappingResult.HumanoidMatchCount;
            result.UniqueNameMatched = mappingResult.UniqueNameMatchCount;
            result.ValidTargetCount = validCount;
            result.SkippedTargetCount = skippedCount;
            result.Succeeded = true;
            return result;
        }
        finally
        {
            if (options.ShowUI)
                EditorUtility.ClearProgressBar();
        }
    }

    public static RebaseResult ExecuteRebaseCore(RebaseOptions options)
    {
        RebaseResult result = new RebaseResult();

        if (!ValidateOptions(options, out string oldGuid, out string newGuid, out List<GameObject> targets, out string error))
        {
            result.Succeeded = false;
            result.ErrorMessage = error;
            return result;
        }

        try
        {
            if (options.ShowUI)
                EditorUtility.DisplayProgressBar("Variant Parent差し替え", "新旧親PrefabのFileID変換表を作成中...", 0.1f);

            ParentMappingResult parentMapping = CreateParentFileIdMapping(
                options.OldBasePrefab,
                options.NewBasePrefab,
                options.UseHumanoidBoneFallback,
                options.UseUniqueNameFallback,
                options.ManualMappings);

            // 対象Prefab群の置換前スナップショットを取得
            List<TargetSnapshot> snapshots = new List<TargetSnapshot>();
            for (int i = 0; i < targets.Count; i++)
            {
                GameObject target = targets[i];
                string targetPath = AssetDatabase.GetAssetPath(target);
                string fullPath = ToFullPath(targetPath);
                if (!File.Exists(fullPath))
                {
                    Debug.LogError($"[PrefabVariantParentReplacer] File not found: {fullPath}");
                    result.SkippedPrefabCount++;
                    continue;
                }

                string content = File.ReadAllText(fullPath, Utf8NoBom);
                if (!content.Contains(oldGuid))
                {
                    Debug.LogWarning($"[PrefabVariantParentReplacer] Old GUID ({oldGuid}) not found in {target.name}. Skipping.");
                    result.SkippedPrefabCount++;
                    continue;
                }

                string targetGuid = AssetDatabase.AssetPathToGUID(targetPath);
                Dictionary<long, AssetRecord> beforeRecords = options.RepairExternalReferences
                    ? CapturePrefabAssetRecords(target, targetPath, targetGuid)
                    : null;

                snapshots.Add(new TargetSnapshot
                {
                    AssetPath = targetPath,
                    FullPath = fullPath,
                    TargetGuid = targetGuid,
                    TargetName = target.name,
                    BeforeRecords = beforeRecords
                });
            }

            if (snapshots.Count == 0)
            {
                result.Succeeded = true;
                return result;
            }

            // 対象PrefabのYAMLを書き換え
            Regex refPattern = new Regex($@"fileID:\s*(?<fid>-?\d+),\s*guid:\s*{Regex.Escape(oldGuid)}");
            Regex fallbackGuidPattern = new Regex($@"guid:\s*{Regex.Escape(oldGuid)}\b");

            for (int i = 0; i < snapshots.Count; i++)
            {
                TargetSnapshot snap = snapshots[i];
                if (options.ShowUI)
                {
                    EditorUtility.DisplayProgressBar(
                        "Variant Parent差し替え",
                        $"{snap.TargetName} を書き換え中... ({i + 1}/{snapshots.Count})",
                        0.15f + 0.35f * (i + 1) / snapshots.Count);
                }

                string content = File.ReadAllText(snap.FullPath, Utf8NoBom);

                if (options.CreateBackupFiles)
                {
                    File.WriteAllText(snap.FullPath + ".bak", content, Utf8NoBom);
                }

                int replacedFileIdCount = 0;
                int unresolvedCount = 0;

                content = refPattern.Replace(content, match =>
                {
                    if (long.TryParse(match.Groups["fid"].Value, out long fid))
                    {
                        if (parentMapping.FileIdMap.TryGetValue(fid, out long newFid))
                        {
                            if (newFid != fid) replacedFileIdCount++;
                            return $"fileID: {newFid}, guid: {newGuid}";
                        }
                        unresolvedCount++;
                    }
                    return $"fileID: {match.Groups["fid"].Value}, guid: {newGuid}";
                });

                content = fallbackGuidPattern.Replace(content, $"guid: {newGuid}");

                File.WriteAllText(snap.FullPath, content, Utf8NoBom);
                result.RebasedPrefabCount++;
                result.FixedParentFileIdCount += replacedFileIdCount;
                result.UnresolvedParentRefCount += unresolvedCount;

                Debug.Log(
                    $"[PrefabVariantParentReplacer] Rebased: {snap.TargetName} " +
                    $"(Parent FileID fixed: {replacedFileIdCount}, Unresolved: {unresolvedCount})");
            }

            // 再インポートして対象Prefabの新しいローカルFileIDを確定
            if (options.ShowUI)
                EditorUtility.DisplayProgressBar("Variant Parent差し替え", "対象Prefabを再インポート中...", 0.55f);

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            // 外部参照の自動修復
            if (options.RepairExternalReferences && result.RebasedPrefabCount > 0)
            {
                Dictionary<string, Dictionary<long, long>> targetGuidToFileIdRemap =
                    new Dictionary<string, Dictionary<long, long>>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < snapshots.Count; i++)
                {
                    TargetSnapshot snap = snapshots[i];
                    if (options.ShowUI)
                    {
                        EditorUtility.DisplayProgressBar(
                            "外部参照の自動修復",
                            $"{snap.TargetName} の新旧FileIDを比較中...",
                            0.6f + 0.1f * (i + 1) / snapshots.Count);
                    }

                    GameObject reloadedTarget = AssetDatabase.LoadAssetAtPath<GameObject>(snap.AssetPath);
                    if (reloadedTarget == null)
                    {
                        Debug.LogWarning($"[PrefabVariantParentReplacer] 再読み込みに失敗しました: {snap.AssetPath}");
                        continue;
                    }

                    Dictionary<long, AssetRecord> afterRecords =
                        CapturePrefabAssetRecords(reloadedTarget, snap.AssetPath, snap.TargetGuid);

                    Dictionary<long, long> remap = BuildTargetFileIdRemap(
                        snap.BeforeRecords,
                        afterRecords,
                        parentMapping.TransformPathMap,
                        options.UseHumanoidBoneFallback,
                        options.UseUniqueNameFallback);

                    if (remap.Count > 0)
                    {
                        targetGuidToFileIdRemap[snap.TargetGuid] = remap;
                        Debug.Log($"[PrefabVariantParentReplacer] {snap.TargetName}: 振り直されたFileIDを {remap.Count} 件検出しました。");
                    }
                }

                if (targetGuidToFileIdRemap.Count > 0)
                {
                    HashSet<string> excludedAssetPaths = new HashSet<string>(
                        snapshots.Select(s => NormalizePath(s.AssetPath)),
                        StringComparer.OrdinalIgnoreCase);

                    RepairExternalAssetReferences(
                        targetGuidToFileIdRemap,
                        excludedAssetPaths,
                        options.ExternalRepairRootAssetFolder,
                        options.CreateBackupFiles,
                        options.ShowUI,
                        out int repairedFilesCount,
                        out int repairedRefsCount,
                        out List<string> modifiedScenePaths);

                    result.RepairedExternalFileCount = repairedFilesCount;
                    result.RepairedExternalRefCount = repairedRefsCount;
                    result.ModifiedScenePaths = modifiedScenePaths;

                    if (repairedFilesCount > 0)
                    {
                        if (options.ShowUI)
                            EditorUtility.DisplayProgressBar("外部参照の自動修復", "修復したアセットを再読み込み中...", 0.95f);

                        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

                        if (options.ReloadModifiedOpenScenes)
                        {
                            ReloadOpenScenesIfModified(modifiedScenePaths);
                        }
                    }
                }
            }

            result.Succeeded = true;
            return result;
        }
        finally
        {
            if (options.ShowUI)
                EditorUtility.ClearProgressBar();
        }
    }

    #endregion

    #region Internal Implementation

    private static bool ValidateOptions(
        RebaseOptions options,
        out string oldGuid,
        out string newGuid,
        out List<GameObject> distinctTargets,
        out string errorMessage)
    {
        oldGuid = null;
        newGuid = null;
        distinctTargets = new List<GameObject>();
        errorMessage = null;

        if (options == null || options.OldBasePrefab == null || options.NewBasePrefab == null ||
            options.TargetPrefabs == null || options.TargetPrefabs.Count == 0)
        {
            errorMessage = "旧Variant Parent、新Variant Parent、および対象Prefabを少なくとも1つ指定してください。";
            return false;
        }

        if (options.OldBasePrefab == options.NewBasePrefab)
        {
            errorMessage = "旧Variant Parent と 新Variant Parent に同じPrefabが指定されています。";
            return false;
        }

        string oldPath = AssetDatabase.GetAssetPath(options.OldBasePrefab);
        string newPath = AssetDatabase.GetAssetPath(options.NewBasePrefab);
        oldGuid = AssetDatabase.AssetPathToGUID(oldPath);
        newGuid = AssetDatabase.AssetPathToGUID(newPath);

        if (string.IsNullOrEmpty(oldGuid) || string.IsNullOrEmpty(newGuid))
        {
            errorMessage = "指定されたVariant ParentのGUIDを取得できませんでした。";
            return false;
        }

        HashSet<string> seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GameObject t in options.TargetPrefabs)
        {
            if (t == null) continue;
            string p = AssetDatabase.GetAssetPath(t);
            if (string.IsNullOrEmpty(p)) continue;
            if (string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p, newPath, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning($"[PrefabVariantParentReplacer] 親Prefab自体が対象に含まれていたため除外しました: {t.name}");
                continue;
            }
            if (seenPaths.Add(p))
            {
                distinctTargets.Add(t);
            }
        }

        if (distinctTargets.Count == 0)
        {
            errorMessage = "処理可能な対象Prefabがありません。";
            return false;
        }

        return true;
    }

    private static UnityEngine.Object ResolvePickedManualObject(UnresolvedEntry entry, UnityEngine.Object picked, bool showDialogs)
    {
        if (entry.IsGameObject)
        {
            if (picked is GameObject go) return go;
            if (picked is Component c) return c.gameObject;
            return picked;
        }

        if (picked is GameObject droppedGo && !string.IsNullOrEmpty(entry.TypeFullName))
        {
            Component[] allComps = droppedGo.GetComponents<Component>();
            List<Component> matching = new List<Component>();
            foreach (Component comp in allComps)
            {
                if (comp == null) continue;
                string fullName = comp.GetType().FullName ?? comp.GetType().Name;
                if (fullName == entry.TypeFullName || comp.GetType().Name == entry.TypeShortName)
                {
                    matching.Add(comp);
                }
            }

            if (matching.Count > 0)
            {
                int idx = Mathf.Clamp(entry.ComponentIndex, 0, matching.Count - 1);
                return matching[idx];
            }

            if (showDialogs)
            {
                EditorUtility.DisplayDialog(
                    "型不一致",
                    $"指定された GameObject ({droppedGo.name}) に '{entry.TypeShortName}' コンポーネントが見つかりませんでした。",
                    "OK");
            }
            return entry.ManualNewObject;
        }

        return picked;
    }

    private static ParentMappingResult CreateParentFileIdMapping(
        GameObject oldBase,
        GameObject newBase,
        bool useHumanoidBoneFallback,
        bool useUniqueNameFallback,
        Dictionary<long, UnityEngine.Object> manualMappings)
    {
        ParentMappingResult result = new ParentMappingResult();
        // Unity の PrefabInstance 予約固定 FileID (m_SourcePrefab 等) は常に同一IDを維持
        result.FileIdMap[100100000L] = 100100000L;

        string oldPath = AssetDatabase.GetAssetPath(oldBase);
        string newPath = AssetDatabase.GetAssetPath(newBase);
        string oldGuid = AssetDatabase.AssetPathToGUID(oldPath);
        string newGuid = AssetDatabase.AssetPathToGUID(newPath);

        result.OldRecordsByFileId = CapturePrefabAssetRecords(oldBase, oldPath, oldGuid);
        result.NewRecordsByFileId = CapturePrefabAssetRecords(newBase, newPath, newGuid);

        Dictionary<string, AssetRecord> newByExactKey = new Dictionary<string, AssetRecord>();
        Dictionary<string, List<AssetRecord>> newGoByName = new Dictionary<string, List<AssetRecord>>();
        Dictionary<string, List<AssetRecord>> oldGoByName = new Dictionary<string, List<AssetRecord>>();
        Dictionary<string, AssetRecord> newByBoneAndComp = new Dictionary<string, AssetRecord>();

        foreach (AssetRecord rec in result.NewRecordsByFileId.Values)
        {
            if (!string.IsNullOrEmpty(rec.ExactKey) && !newByExactKey.ContainsKey(rec.ExactKey))
            {
                newByExactKey[rec.ExactKey] = rec;
            }

            if (rec.IsGameObject)
            {
                if (!newGoByName.TryGetValue(rec.ObjectName, out List<AssetRecord> list))
                {
                    list = new List<AssetRecord>();
                    newGoByName[rec.ObjectName] = list;
                }
                list.Add(rec);
            }

            if (rec.HumanoidBone != HumanBodyBones.LastBone)
            {
                string boneKey = BuildBoneCompositeKey(rec);
                if (!newByBoneAndComp.ContainsKey(boneKey))
                {
                    newByBoneAndComp[boneKey] = rec;
                }
            }
        }

        foreach (AssetRecord rec in result.OldRecordsByFileId.Values)
        {
            if (rec.IsGameObject)
            {
                if (!oldGoByName.TryGetValue(rec.ObjectName, out List<AssetRecord> list))
                {
                    list = new List<AssetRecord>();
                    oldGoByName[rec.ObjectName] = list;
                }
                list.Add(rec);
            }
        }

        // Step 0: 手動マッピングを最優先で反映
        Dictionary<string, string> manualPathMap = new Dictionary<string, string>();
        if (manualMappings != null)
        {
            foreach (KeyValuePair<long, UnityEngine.Object> kv in manualMappings)
            {
                if (kv.Value == null) continue;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(kv.Value, out string guid, out long newFid) &&
                    string.Equals(guid, newGuid, StringComparison.OrdinalIgnoreCase))
                {
                    result.FileIdMap[kv.Key] = newFid;
                    result.ManualMatchCount++;

                    if (result.OldRecordsByFileId.TryGetValue(kv.Key, out AssetRecord oldRec) &&
                        result.NewRecordsByFileId.TryGetValue(newFid, out AssetRecord newRec))
                    {
                        manualPathMap[oldRec.TransformPath] = newRec.TransformPath;
                        result.TransformPathMap[oldRec.TransformPath] = newRec.TransformPath;
                    }
                }
            }

            if (manualPathMap.Count > 0)
            {
                foreach (AssetRecord oldRec in result.OldRecordsByFileId.Values)
                {
                    if (result.FileIdMap.ContainsKey(oldRec.LocalFileId)) continue;
                    if (manualPathMap.TryGetValue(oldRec.TransformPath, out string mappedNewPath))
                    {
                        string remappedKey = oldRec.IsGameObject
                            ? BuildGameObjectKey(mappedNewPath)
                            : BuildComponentKey(mappedNewPath, oldRec.TypeFullName, oldRec.ComponentIndex);

                        if (newByExactKey.TryGetValue(remappedKey, out AssetRecord newRec))
                        {
                            result.FileIdMap[oldRec.LocalFileId] = newRec.LocalFileId;
                            result.ManualMatchCount++;
                        }
                    }
                }
            }
        }

        // Step 1: 完全一致 (TransformPath + Type + Index)
        foreach (AssetRecord oldRec in result.OldRecordsByFileId.Values)
        {
            if (result.FileIdMap.ContainsKey(oldRec.LocalFileId)) continue;

            if (!string.IsNullOrEmpty(oldRec.ExactKey) &&
                newByExactKey.TryGetValue(oldRec.ExactKey, out AssetRecord newRec))
            {
                result.FileIdMap[oldRec.LocalFileId] = newRec.LocalFileId;
                result.ExactMatchCount++;
                if (!result.TransformPathMap.ContainsKey(oldRec.TransformPath))
                {
                    result.TransformPathMap[oldRec.TransformPath] = newRec.TransformPath;
                }
            }
        }

        // Step 2: Humanoidボーン一致（オプション）
        if (useHumanoidBoneFallback)
        {
            foreach (AssetRecord oldRec in result.OldRecordsByFileId.Values)
            {
                if (result.FileIdMap.ContainsKey(oldRec.LocalFileId)) continue;
                if (oldRec.HumanoidBone == HumanBodyBones.LastBone) continue;

                string boneKey = BuildBoneCompositeKey(oldRec);
                if (newByBoneAndComp.TryGetValue(boneKey, out AssetRecord newRec))
                {
                    result.FileIdMap[oldRec.LocalFileId] = newRec.LocalFileId;
                    result.HumanoidMatchCount++;
                    if (!result.TransformPathMap.ContainsKey(oldRec.TransformPath))
                    {
                        result.TransformPathMap[oldRec.TransformPath] = newRec.TransformPath;
                    }
                }
            }
        }

        // Step 3: 一意なGameObject名によるフォールバック照合（オプション）
        if (useUniqueNameFallback)
        {
            Dictionary<string, string> uniqueGoPathMap = new Dictionary<string, string>();
            foreach (KeyValuePair<string, List<AssetRecord>> kv in oldGoByName)
            {
                if (kv.Value.Count != 1) continue;
                if (!newGoByName.TryGetValue(kv.Key, out List<AssetRecord> newList) || newList.Count != 1) continue;

                AssetRecord oldGoRec = kv.Value[0];
                AssetRecord newGoRec = newList[0];

                if (!result.FileIdMap.ContainsKey(oldGoRec.LocalFileId))
                {
                    result.FileIdMap[oldGoRec.LocalFileId] = newGoRec.LocalFileId;
                    result.UniqueNameMatchCount++;
                }

                uniqueGoPathMap[oldGoRec.TransformPath] = newGoRec.TransformPath;
                if (!result.TransformPathMap.ContainsKey(oldGoRec.TransformPath))
                {
                    result.TransformPathMap[oldGoRec.TransformPath] = newGoRec.TransformPath;
                }
            }

            foreach (AssetRecord oldRec in result.OldRecordsByFileId.Values)
            {
                if (result.FileIdMap.ContainsKey(oldRec.LocalFileId)) continue;
                if (oldRec.IsGameObject) continue;

                if (uniqueGoPathMap.TryGetValue(oldRec.TransformPath, out string mappedNewPath))
                {
                    string remappedKey = BuildComponentKey(mappedNewPath, oldRec.TypeFullName, oldRec.ComponentIndex);
                    if (newByExactKey.TryGetValue(remappedKey, out AssetRecord newRec))
                    {
                        result.FileIdMap[oldRec.LocalFileId] = newRec.LocalFileId;
                        result.UniqueNameMatchCount++;
                    }
                }
            }
        }

        return result;
    }

    private static Dictionary<long, AssetRecord> CapturePrefabAssetRecords(GameObject rootPrefab, string assetPath, string expectedGuid)
    {
        Dictionary<long, AssetRecord> records = new Dictionary<long, AssetRecord>();
        if (rootPrefab == null || string.IsNullOrEmpty(assetPath)) return records;

        Dictionary<Transform, HumanBodyBones> boneMap = BuildHumanoidBoneMap(rootPrefab);
        UnityEngine.Object[] allAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);

        foreach (UnityEngine.Object asset in allAssets)
        {
            if (asset == null) continue;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localFileId))
                continue;

            if (!string.IsNullOrEmpty(expectedGuid) && !string.Equals(guid, expectedGuid, StringComparison.OrdinalIgnoreCase))
                continue;

            if (records.ContainsKey(localFileId)) continue;

            if (asset is GameObject go)
            {
                string path = AnimationUtility.CalculateTransformPath(go.transform, rootPrefab.transform);
                HumanBodyBones bone = HumanBodyBones.LastBone;
                boneMap.TryGetValue(go.transform, out bone);

                records[localFileId] = new AssetRecord
                {
                    Asset = go,
                    LocalFileId = localFileId,
                    IsGameObject = true,
                    TransformPath = path,
                    ObjectName = go.name,
                    TypeFullName = "UnityEngine.GameObject",
                    TypeShortName = "GameObject",
                    ComponentIndex = 0,
                    ExactKey = BuildGameObjectKey(path),
                    HumanoidBone = bone
                };
            }
            else if (asset is Component comp)
            {
                string path = AnimationUtility.CalculateTransformPath(comp.transform, rootPrefab.transform);
                Type compType = comp.GetType();
                string fullName = compType.FullName ?? compType.Name;
                Component[] sameTypeComps = comp.gameObject.GetComponents(compType);
                int index = Array.IndexOf(sameTypeComps, comp);

                HumanBodyBones bone = HumanBodyBones.LastBone;
                boneMap.TryGetValue(comp.transform, out bone);

                records[localFileId] = new AssetRecord
                {
                    Asset = comp,
                    LocalFileId = localFileId,
                    IsGameObject = false,
                    TransformPath = path,
                    ObjectName = comp.gameObject.name,
                    TypeFullName = fullName,
                    TypeShortName = compType.Name,
                    ComponentIndex = index,
                    ExactKey = BuildComponentKey(path, fullName, index),
                    HumanoidBone = bone
                };
            }
        }

        return records;
    }

    private static Dictionary<long, long> BuildTargetFileIdRemap(
        Dictionary<long, AssetRecord> beforeRecords,
        Dictionary<long, AssetRecord> afterRecords,
        Dictionary<string, string> parentTransformPathMap,
        bool useHumanoidBoneFallback,
        bool useUniqueNameFallback)
    {
        Dictionary<long, long> remap = new Dictionary<long, long>();
        if (beforeRecords == null || afterRecords == null) return remap;

        Dictionary<string, AssetRecord> afterByExactKey = new Dictionary<string, AssetRecord>();
        Dictionary<string, AssetRecord> afterByBoneKey = new Dictionary<string, AssetRecord>();
        Dictionary<string, List<AssetRecord>> beforeByUniqueSig = new Dictionary<string, List<AssetRecord>>();
        Dictionary<string, List<AssetRecord>> afterByUniqueSig = new Dictionary<string, List<AssetRecord>>();

        foreach (AssetRecord rec in afterRecords.Values)
        {
            if (!afterByExactKey.ContainsKey(rec.ExactKey))
            {
                afterByExactKey[rec.ExactKey] = rec;
            }
            if (rec.HumanoidBone != HumanBodyBones.LastBone)
            {
                string boneKey = BuildBoneCompositeKey(rec);
                if (!afterByBoneKey.ContainsKey(boneKey))
                {
                    afterByBoneKey[boneKey] = rec;
                }
            }

            string sig = $"{rec.ObjectName}|{rec.TypeFullName}|{rec.ComponentIndex}";
            if (!afterByUniqueSig.TryGetValue(sig, out List<AssetRecord> list))
            {
                list = new List<AssetRecord>();
                afterByUniqueSig[sig] = list;
            }
            list.Add(rec);
        }

        foreach (AssetRecord rec in beforeRecords.Values)
        {
            string sig = $"{rec.ObjectName}|{rec.TypeFullName}|{rec.ComponentIndex}";
            if (!beforeByUniqueSig.TryGetValue(sig, out List<AssetRecord> list))
            {
                list = new List<AssetRecord>();
                beforeByUniqueSig[sig] = list;
            }
            list.Add(rec);
        }

        foreach (AssetRecord before in beforeRecords.Values)
        {
            AssetRecord matchedAfter = null;

            if (afterByExactKey.TryGetValue(before.ExactKey, out AssetRecord exactMatch))
            {
                matchedAfter = exactMatch;
            }
            else if (parentTransformPathMap != null &&
                     TryRemapTransformPath(before.TransformPath, parentTransformPathMap, out string mappedPath))
            {
                string remappedKey = before.IsGameObject
                    ? BuildGameObjectKey(mappedPath)
                    : BuildComponentKey(mappedPath, before.TypeFullName, before.ComponentIndex);

                if (afterByExactKey.TryGetValue(remappedKey, out AssetRecord pathMatch))
                {
                    matchedAfter = pathMatch;
                }
            }

            if (matchedAfter == null && useHumanoidBoneFallback && before.HumanoidBone != HumanBodyBones.LastBone)
            {
                string boneKey = BuildBoneCompositeKey(before);
                if (afterByBoneKey.TryGetValue(boneKey, out AssetRecord boneMatch))
                {
                    matchedAfter = boneMatch;
                }
            }

            if (matchedAfter == null && useUniqueNameFallback)
            {
                string sig = $"{before.ObjectName}|{before.TypeFullName}|{before.ComponentIndex}";
                if (beforeByUniqueSig.TryGetValue(sig, out List<AssetRecord> bList) && bList.Count == 1 &&
                    afterByUniqueSig.TryGetValue(sig, out List<AssetRecord> aList) && aList.Count == 1)
                {
                    matchedAfter = aList[0];
                }
            }

            if (matchedAfter != null && before.LocalFileId != matchedAfter.LocalFileId)
            {
                remap[before.LocalFileId] = matchedAfter.LocalFileId;
            }
        }

        return remap;
    }

    private static bool TryRemapTransformPath(string originalPath, Dictionary<string, string> pathMap, out string remappedPath)
    {
        if (pathMap.TryGetValue(originalPath, out remappedPath))
        {
            return true;
        }

        string bestPrefix = null;
        string bestReplacement = null;
        foreach (KeyValuePair<string, string> kv in pathMap)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            string prefixWithSlash = kv.Key + "/";
            if (originalPath.StartsWith(prefixWithSlash, StringComparison.Ordinal))
            {
                if (bestPrefix == null || kv.Key.Length > bestPrefix.Length)
                {
                    bestPrefix = kv.Key;
                    bestReplacement = kv.Value;
                }
            }
        }

        if (bestPrefix != null)
        {
            string suffix = originalPath.Substring(bestPrefix.Length);
            remappedPath = string.IsNullOrEmpty(bestReplacement)
                ? suffix.TrimStart('/')
                : bestReplacement + suffix;
            return true;
        }

        remappedPath = null;
        return false;
    }

    private static void RepairExternalAssetReferences(
        Dictionary<string, Dictionary<long, long>> targetGuidToFileIdRemap,
        HashSet<string> excludedAssetPaths,
        string rootAssetFolder,
        bool createBackupFiles,
        bool showUI,
        out int repairedFilesCount,
        out int repairedRefsCount,
        out List<string> modifiedScenePaths)
    {
        repairedFilesCount = 0;
        repairedRefsCount = 0;
        modifiedScenePaths = new List<string>();

        string projectRoot = Directory.GetCurrentDirectory();
        string scanRootDir = string.IsNullOrEmpty(rootAssetFolder)
            ? Application.dataPath
            : Path.Combine(projectRoot, rootAssetFolder);

        if (!Directory.Exists(scanRootDir))
        {
            Debug.LogWarning($"[PrefabVariantParentReplacer] スキャン対象フォルダが存在しません: {scanRootDir}");
            return;
        }

        List<string> candidateFiles = new List<string>();
        foreach (string file in Directory.EnumerateFiles(scanRootDir, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file);
            if (!ExternalRepairExtensions.Contains(ext)) continue;

            string relativePath = NormalizePath(ToRelativePath(file, projectRoot));
            if (excludedAssetPaths.Contains(relativePath)) continue;

            candidateFiles.Add(file);
        }

        List<KeyValuePair<string, Regex>> guidRegexList = new List<KeyValuePair<string, Regex>>();
        foreach (string targetGuid in targetGuidToFileIdRemap.Keys)
        {
            Regex rx = new Regex($@"fileID:\s*(?<fid>-?\d+),\s*guid:\s*{Regex.Escape(targetGuid)}", RegexOptions.Compiled);
            guidRegexList.Add(new KeyValuePair<string, Regex>(targetGuid, rx));
        }

        int totalFiles = candidateFiles.Count;
        for (int i = 0; i < totalFiles; i++)
        {
            string filePath = candidateFiles[i];
            if (showUI && (i % 50 == 0 || i == totalFiles - 1))
            {
                EditorUtility.DisplayProgressBar(
                    "外部参照の自動修復",
                    $"プロジェクト内の参照をスキャン中... ({i + 1}/{totalFiles})",
                    0.7f + 0.23f * (i + 1) / Math.Max(1, totalFiles));
            }

            string content;
            try
            {
                content = File.ReadAllText(filePath, Utf8NoBom);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PrefabVariantParentReplacer] 読み込みをスキップしました: {filePath} ({ex.Message})");
                continue;
            }

            if (content.IndexOf('\0') >= 0) continue;

            bool containsAnyTargetGuid = false;
            for (int g = 0; g < guidRegexList.Count; g++)
            {
                if (content.IndexOf(guidRegexList[g].Key, StringComparison.Ordinal) >= 0)
                {
                    containsAnyTargetGuid = true;
                    break;
                }
            }
            if (!containsAnyTargetGuid) continue;

            int fileReplacedCount = 0;
            string updatedContent = content;

            for (int g = 0; g < guidRegexList.Count; g++)
            {
                string targetGuid = guidRegexList[g].Key;
                if (updatedContent.IndexOf(targetGuid, StringComparison.Ordinal) < 0) continue;

                Dictionary<long, long> remap = targetGuidToFileIdRemap[targetGuid];
                updatedContent = guidRegexList[g].Value.Replace(updatedContent, match =>
                {
                    if (long.TryParse(match.Groups["fid"].Value, out long oldFid) &&
                        remap.TryGetValue(oldFid, out long newFid))
                    {
                        fileReplacedCount++;
                        return $"fileID: {newFid}, guid: {targetGuid}";
                    }
                    return match.Value;
                });
            }

            if (fileReplacedCount > 0)
            {
                if (createBackupFiles)
                {
                    File.WriteAllText(filePath + ".bak", content, Utf8NoBom);
                }
                File.WriteAllText(filePath, updatedContent, Utf8NoBom);
                repairedFilesCount++;
                repairedRefsCount += fileReplacedCount;

                string relPath = NormalizePath(ToRelativePath(filePath, projectRoot));
                if (Path.GetExtension(filePath).Equals(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    modifiedScenePaths.Add(relPath);
                }

                Debug.Log($"[PrefabVariantParentReplacer] 外部参照を修復しました: {relPath} ({fileReplacedCount} 箇所)");
            }
        }
    }

    private static void ReloadOpenScenesIfModified(List<string> modifiedScenePaths)
    {
        if (modifiedScenePaths == null || modifiedScenePaths.Count == 0) return;

        HashSet<string> modifiedSet = new HashSet<string>(modifiedScenePaths, StringComparer.OrdinalIgnoreCase);
        List<OpenSceneState> openScenes = new List<OpenSceneState>();
        Scene activeScene = SceneManager.GetActiveScene();
        bool needReload = false;

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene s = SceneManager.GetSceneAt(i);
            string normPath = NormalizePath(s.path);
            if (!string.IsNullOrEmpty(normPath) && modifiedSet.Contains(normPath))
            {
                needReload = true;
            }
            openScenes.Add(new OpenSceneState
            {
                Path = normPath,
                IsLoaded = s.isLoaded,
                IsActive = (s == activeScene)
            });
        }

        if (!needReload) return;

        Scene? restoredActive = null;
        bool openedFirst = false;

        foreach (OpenSceneState state in openScenes)
        {
            if (string.IsNullOrEmpty(state.Path)) continue;

            OpenSceneMode mode;
            if (!openedFirst)
            {
                mode = OpenSceneMode.Single;
                openedFirst = true;
            }
            else
            {
                mode = state.IsLoaded ? OpenSceneMode.Additive : OpenSceneMode.AdditiveWithoutLoading;
            }

            Scene reopened = EditorSceneManager.OpenScene(state.Path, mode);
            if (state.IsActive)
            {
                restoredActive = reopened;
            }
        }

        if (restoredActive.HasValue && restoredActive.Value.IsValid())
        {
            SceneManager.SetActiveScene(restoredActive.Value);
        }
    }

    private static Dictionary<Transform, HumanBodyBones> BuildHumanoidBoneMap(GameObject rootPrefab)
    {
        Dictionary<Transform, HumanBodyBones> map = new Dictionary<Transform, HumanBodyBones>();
        if (rootPrefab == null) return map;

        Animator animator = rootPrefab.GetComponentInChildren<Animator>(true);
        if (animator == null || !animator.isHuman) return map;

        for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
        {
            HumanBodyBones bone = (HumanBodyBones)i;
            Transform t = animator.GetBoneTransform(bone);
            if (t != null && !map.ContainsKey(t))
            {
                map[t] = bone;
            }
        }

        return map;
    }

    private static bool IsAssetInsidePrefab(UnityEngine.Object obj, GameObject prefab)
    {
        if (obj == null || prefab == null) return false;
        string objPath = AssetDatabase.GetAssetPath(obj);
        string prefabPath = AssetDatabase.GetAssetPath(prefab);
        return !string.IsNullOrEmpty(objPath) &&
               string.Equals(objPath, prefabPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildGameObjectKey(string transformPath)
    {
        return transformPath + "_GameObject";
    }

    private static string BuildComponentKey(string transformPath, string typeFullName, int componentIndex)
    {
        return $"{transformPath}_{typeFullName}_{componentIndex}";
    }

    private static string BuildBoneCompositeKey(AssetRecord rec)
    {
        return rec.IsGameObject
            ? $"BONE:{rec.HumanoidBone}_GameObject"
            : $"BONE:{rec.HumanoidBone}_{rec.TypeFullName}_{rec.ComponentIndex}";
    }

    private static string ToFullPath(string assetPath)
    {
        return Path.Combine(Directory.GetCurrentDirectory(), assetPath);
    }

    private static string ToRelativePath(string fullPath, string projectRoot)
    {
        string normFull = Path.GetFullPath(fullPath);
        string normRoot = Path.GetFullPath(projectRoot);
        if (!normRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
        {
            normRoot += Path.DirectorySeparatorChar;
        }
        if (normFull.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
        {
            return normFull.Substring(normRoot.Length);
        }
        return normFull;
    }

    private static string NormalizePath(string path)
    {
        return string.IsNullOrEmpty(path) ? "" : path.Replace('\\', '/');
    }

    #endregion
}
