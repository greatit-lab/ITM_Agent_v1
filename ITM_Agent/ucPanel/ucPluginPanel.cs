// ITM_Agent/ucPanel/ucPluginPanel.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using ITM_Agent.Plugins;
using ITM_Agent.Services;
using System.Text.RegularExpressions;         // [추가] 플러그인명 정규식 파싱

namespace ITM_Agent.ucPanel
{
    public partial class ucPluginPanel : UserControl
    {
        // 플러그인 정보를 보관하는 리스트 (PluginListItem은 플러그인명과 DLL 경로 정보를 저장)
        private List<PluginListItem> loadedPlugins = new List<PluginListItem>();
        private SettingsManager settingsManager;
        private LogManager logManager;

        // 플러그인 리스트가 변경될 때 통보용
        public event EventHandler PluginsChanged;

        public ucPluginPanel(SettingsManager settings)
        {
            InitializeComponent();
            settingsManager = settings;
            logManager = new LogManager(AppDomain.CurrentDomain.BaseDirectory);

            // settings.ini의 [RegPlugins] 섹션에서 기존에 등록된 플러그인 정보를 불러옴
            LoadPluginsFromSettings();
        }

        private void UpdatePluginListDisplay()
        {
            lb_PluginList.Items.Clear();
            for (int i = 0; i < loadedPlugins.Count; i++)
            {
                lb_PluginList.Items.Add($"{i + 1}. {loadedPlugins[i]}");
            }
        }

        private void btn_PlugAdd_Click(object sender, EventArgs e)
        {
            /* 1) 파일 선택 대화상자 (전통적 using 블록) */
            using (OpenFileDialog open = new OpenFileDialog
            {
                Filter = "DLL Files (*.dll)|*.dll|All Files (*.*)|*.*",
                InitialDirectory = AppDomain.CurrentDomain.BaseDirectory,
                Multiselect = true // ▼▼▼ [수정] 다중 선택 허용 ▼▼▼
            })
            {
                if (open.ShowDialog() != DialogResult.OK) return;

                // ▼▼▼ [추가] 다중 처리를 위한 카운터 ▼▼▼
                int addedCount = 0;
                int skippedCount = 0;
                List<string> errorMessages = new List<string>();

                // ▼▼▼ [수정] 선택된 모든 파일을 순회 ▼▼▼
                foreach (string selectedDllPath in open.FileNames)
                {
                    try
                    {
                        /* 2) Assembly 로드 & 중복 체크 */
                        byte[] dllBytes = File.ReadAllBytes(selectedDllPath);
                        Assembly asm = Assembly.Load(dllBytes);
                        string pluginName = asm.GetName().Name;

                        if (loadedPlugins.Any(p => p.PluginName.Equals(pluginName, StringComparison.OrdinalIgnoreCase)))
                        {
                            logManager.LogEvent($"Plugin add skipped (already registered): {pluginName}");
                            skippedCount++;
                            continue; // [수정] 중복 시 다음 파일로
                        }

                        /* 3) Library 폴더 준비 */
                        string libraryFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Library");
                        if (!Directory.Exists(libraryFolder)) Directory.CreateDirectory(libraryFolder);

                        /* 4) 플러그인 DLL 복사 */
                        string destDllPath = Path.Combine(libraryFolder, Path.GetFileName(selectedDllPath));
                        if (File.Exists(destDllPath))
                        {
                             logManager.LogEvent($"Plugin add skipped (file already exists in Library): {Path.GetFileName(selectedDllPath)}");
                             skippedCount++;
                             continue;
                        }
                        File.Copy(selectedDllPath, destDllPath);

                        /* 5) 참조 DLL 자동 복사 */
                        foreach (AssemblyName refAsm in asm.GetReferencedAssemblies())
                        {
                            string refFile = refAsm.Name + ".dll";
                            string srcRef = Path.Combine(Path.GetDirectoryName(selectedDllPath), refFile);
                            string dstRef = Path.Combine(libraryFolder, refFile);

                            if (File.Exists(srcRef) && !File.Exists(dstRef))
                                File.Copy(srcRef, dstRef);
                        }

                        /* 6) 필수 NuGet DLL 강제 복사 (CodePages 등) */
                        string[] mustHave = { "System.Text.Encoding.CodePages.dll" };
                        foreach (var f in mustHave)
                        {
                            string src = Path.Combine(Path.GetDirectoryName(selectedDllPath), f);
                            string dst = Path.Combine(libraryFolder, f);
                            if (File.Exists(src) && !File.Exists(dst))
                                File.Copy(src, dst);
                        }

                        /* 7) 목록 설정 등록 */
                        var version = asm.GetName().Version.ToString();
                        var item = new PluginListItem
                        {
                            PluginName = pluginName,
                            PluginVersion = version,
                            AssemblyPath = destDllPath
                        };

                        loadedPlugins.Add(item);
                        SavePluginInfoToSettings(item);
                        logManager.LogEvent($"Plugin registered: {pluginName}");
                        addedCount++;
                    }
                    catch (Exception ex)
                    {
                        errorMessages.Add($"Error loading {Path.GetFileName(selectedDllPath)}: {ex.Message}");
                        logManager.LogError($"Plugin load error: {ex}");
                    }
                } // ▲▲▲ [수정] foreach 루프 끝 ▲▲▲

                // ▼▼▼ [수정] 루프 종료 후 한 번만 UI 갱신 및 알림 ▼▼▼
                if (addedCount > 0)
                {
                    UpdatePluginListDisplay();
                    PluginsChanged?.Invoke(this, EventArgs.Empty);
                }

                // [수정] 최종 결과 알림
                if (errorMessages.Count > 0)
                {
                    MessageBox.Show($"Errors occurred:\n{string.Join("\n", errorMessages)}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else if (addedCount > 0 || skippedCount > 0)
                {
                    MessageBox.Show($"플러그인 추가 완료.\n\n- 성공: {addedCount}개\n- 중복/스킵: {skippedCount}개", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                // ▲▲▲ [수정] 완료 ▲▲▲
            }   // using OpenFileDialog
        }

        private void btn_PlugRemove_Click(object sender, EventArgs e)
        {
            // ▼▼▼ [수정] 다중 선택 기준으로 변경 ▼▼▼
            if (lb_PluginList.SelectedItems.Count == 0)
            {
                MessageBox.Show("삭제할 플러그인을 선택하세요.", "알림",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // [수정] 선택된 항목 목록 가져오기
            var selectedDisplayItems = lb_PluginList.SelectedItems.Cast<string>().ToList();

            DialogResult result = MessageBox.Show(
                $"선택한 {selectedDisplayItems.Count}개의 플러그인을 삭제하시겠습니까?", // [수정] 확인 메시지
                "삭제 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result != DialogResult.Yes) return;

            int removedCount = 0;
            List<string> errorMessages = new List<string>();

            // ▼▼▼ [수정] 선택된 모든 항목을 순회하며 삭제 ▼▼▼
            foreach (string display in selectedDisplayItems)
            {
                // 1) ListBox 표시 문자열 → “플러그인명” 추출 ─ 번호와 (v버전) 제거
                string pluginName = Regex.Replace(display, @"^\d+\.\s*", "");     // 앞쪽 "번호. " 삭제
                pluginName = Regex.Replace(pluginName, @"\s*\(v.*\)$", "");       // 뒤쪽 "(v…)" 삭제
                pluginName = pluginName.Trim();

                // 2) loadedPlugins 검색
                var pluginItem = loadedPlugins
                                 .FirstOrDefault(p => p.PluginName.Equals(pluginName,
                                                         StringComparison.OrdinalIgnoreCase));
                if (pluginItem == null)
                {
                    errorMessages.Add($"Internal list error: '{pluginName}' not found.");
                    logManager.LogError($"Remove failed (not in list): {pluginName}");
                    continue; // 다음 항목으로
                }

                // 3) DLL 파일 삭제
                if (File.Exists(pluginItem.AssemblyPath))
                {
                    try
                    {
                        File.Delete(pluginItem.AssemblyPath);
                        logManager.LogEvent($"DLL deleted: {pluginItem.AssemblyPath}");
                    }
                    catch (Exception ex)
                    {
                        errorMessages.Add($"DLL delete error '{pluginName}': {ex.Message}");
                        logManager.LogError("DLL 삭제 오류: " + ex.Message);
                        continue; // 파일 삭제 실패 시 목록에서 제거하지 않고 다음 항목으로
                    }
                }

                // 4) 내부 리스트 & INI 제거
                loadedPlugins.Remove(pluginItem);
                settingsManager.RemoveKeyFromSection("RegPlugins", pluginName);
                logManager.LogEvent($"Plugin removed: {pluginName}");
                removedCount++;
            }
            // ▲▲▲ [수정] foreach 루프 끝 ▲▲▲

            // 5) UI 재갱신 (루프 종료 후 한 번만)
            if (removedCount > 0)
            {
                UpdatePluginListDisplay();
                PluginsChanged?.Invoke(this, EventArgs.Empty);
            }

            // [추가] 오류 알림
            if (errorMessages.Count > 0)
            {
                MessageBox.Show($"Errors occurred:\n{string.Join("\n", errorMessages)}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// settings.ini의 [RegPlugins] 섹션에 플러그인 정보를 기록합니다.
        /// 형식 예시:
        /// [RegPlugins]
        /// MyPlugin = C:\... \Library\MyPlugin.dll
        /// </summary>
        private void SavePluginInfoToSettings(PluginListItem pluginItem)
        {
            // (1)  플러그인 DLL은 항상 BaseDir\Library 에 복사되므로
            //      ini 파일에는 "Library\파일명.dll" 만 저장
            string relativePath = Path.Combine("Library", Path.GetFileName(pluginItem.AssemblyPath));

            // (2)  Settings.ini → [RegPlugins] 섹션에 기록
            settingsManager.SetValueToSection("RegPlugins",
                pluginItem.PluginName,
                relativePath);                       //  ← Library\Onto_WaferFlatData.dll
        }

        private void LoadPluginsFromSettings()
        {
            // ① [RegPlugins] 섹션 라인 전체 읽기
            var pluginEntries = settingsManager.GetFoldersFromSection("[RegPlugins]");
            foreach (string entry in pluginEntries)
            {
                // "PluginName = AssemblyPath" 형식 파싱
                string[] parts = entry.Split(new[] { '=' }, 2);
                if (parts.Length != 2) continue;

                string iniKeyName = parts[0].Trim();   // INI에 기록된 키(=플러그인명)
                string assemblyPath = parts[1].Trim();   // 상대 or 절대 경로

                // ② 상대 경로 → 절대 경로 변환
                if (!Path.IsPathRooted(assemblyPath))
                    assemblyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, assemblyPath);

                if (!File.Exists(assemblyPath))
                {
                    logManager.LogError($"플러그인 DLL을 찾을 수 없습니다: {assemblyPath}");
                    continue;
                }

                try
                {
                    /* ③ DLL 메모리 로드 → 파일 잠금 방지 */
                    byte[] dllBytes = File.ReadAllBytes(assemblyPath);
                    Assembly asm = Assembly.Load(dllBytes);

                    /* ④ 어셈블리 메타데이터 추출 */
                    string asmName = asm.GetName().Name;              // 실제 어셈블리 이름
                    string asmVersion = asm.GetName().Version.ToString();// 버전 문자열

                    /* ⑤ PluginListItem 구성 */
                    var item = new PluginListItem
                    {
                        PluginName = asmName,
                        PluginVersion = asmVersion,
                        AssemblyPath = assemblyPath
                    };

                    loadedPlugins.Add(item);                 // 내부 리스트 보존
                    logManager.LogEvent($"Plugin auto-loaded: {item}");
                }
                catch (Exception ex)
                {
                    logManager.LogError($"플러그인 로드 실패: {ex.Message}");
                }
                UpdatePluginListDisplay();
            }
        }

        /// <summary>
        /// 외부에서 로드된 플러그인 목록을 반환합니다.
        /// </summary>
        public List<PluginListItem> GetLoadedPlugins()
        {
            return loadedPlugins;
        }

        #region ====== Run 상태 동기화 ======   // [추가]

        /// <summary>
        /// 각 컨트롤의 Enable 상태를 일괄 변경한다.
        /// </summary>
        private void SetControlsEnabled(bool enabled)         // [추가]
        {
            btn_PlugAdd.Enabled = enabled;
            btn_PlugRemove.Enabled = enabled;
            lb_PluginList.Enabled = enabled;
        }

        /// <summary>
        /// MainForm 에서 Run/Stop 전환 시 호출된다.
        /// </summary>
        public void UpdateStatusOnRun(bool isRunning)         // [추가]
        {
            SetControlsEnabled(!isRunning);
        }

        /// <summary>
        /// 처음 패널을 화면에 띄울 때, 혹은 MainForm 에서 상태를 다시 맞출 때 호출.
        /// </summary>
        public void InitializePanel(bool isRunning)           // [추가]
        {
            SetControlsEnabled(!isRunning);
        }

        #endregion
    }
}
