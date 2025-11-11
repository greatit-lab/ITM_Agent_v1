// ITM_Agent/ucPanel/ucUploadPanel.cs
using ITM_Agent.Plugins;
using ITM_Agent.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ITM_Agent.Properties;

namespace ITM_Agent.ucPanel
{
    public partial class ucUploadPanel : UserControl
    {
        // --- Watcher 목록 관리 ---
        private readonly List<FileSystemWatcher> _tab1Watchers = new List<FileSystemWatcher>();
        private readonly List<FileSystemWatcher> _tab2Watchers = new List<FileSystemWatcher>();
        private readonly ConcurrentDictionary<string, string> _tab1RuleMap = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _tab2RuleMap = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lockTab1 = new object();
        private readonly object _lockTab2 = new object();

        // [핵심 수정] "처리 중" 플래그 대신 "마지막 이벤트 시간"을 기록
        private readonly ConcurrentDictionary<string, DateTime> _lastProcessEventTime =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private const double DebounceSeconds = 2.0; // 2초 이내의 중복 이벤트는 무시

        // --- 참조 필드 ---
        private readonly ucConfigurationPanel _configPanel;
        private readonly ucPluginPanel _pluginPanel;
        private readonly SettingsManager _settingsManager;
        private readonly LogManager _logManager;
        private readonly ucOverrideNamesPanel _overridePanel;
        private readonly ucImageTransPanel _imageTransPanel;

        // --- 플러그인 메타데이터 캐시 ---
        private readonly Dictionary<string, (string Task, string Filter)> _pluginMetadataCache =
            new Dictionary<string, (string Task, string Filter)>(StringComparer.OrdinalIgnoreCase);

        // --- INI 섹션 이름 ---
        private const string Tab1Section = "[UploadRulesTab1]";
        private const string Tab2Section = "[UploadRulesTab2]";

        public ucUploadPanel(ucConfigurationPanel configPanel, ucPluginPanel pluginPanel, SettingsManager settingsManager,
            ucOverrideNamesPanel ovPanel, ucImageTransPanel imageTransPanel)
        {
            InitializeComponent();

            _configPanel = configPanel ?? throw new ArgumentNullException(nameof(configPanel));
            _pluginPanel = pluginPanel ?? throw new ArgumentNullException(nameof(pluginPanel));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _overridePanel = ovPanel;
            _imageTransPanel = imageTransPanel ?? throw new ArgumentNullException(nameof(imageTransPanel));

            _logManager = new LogManager(AppDomain.CurrentDomain.BaseDirectory);

            this.Load += UcUploadPanel_Load;
            _pluginPanel.PluginsChanged += OnPluginsChanged;

            // 탭 1 버튼
            btnCatAdd.Click += BtnCatAdd_Click;
            btnCatRemove.Click += BtnCatRemove_Click;
            btnCatSave.Click += BtnCatSave_Click;

            // 탭 2 버튼
            btnLiveAdd.Click += BtnLiveAdd_Click;
            btnLiveRemove.Click += BtnLiveRemove_Click;
            btnLiveSave.Click += BtnLiveSave_Click;

            InitializeDataGridViews();
            LoadSettings();

            // [수정] CellValueChanged: 자동 완성을 위해 유지
            dgvCategorized.CellValueChanged += Dgv_CellValueChanged;
            dgvLiveMonitoring.CellValueChanged += Dgv_CellValueChanged;

            dgvCategorized.CellFormatting += Dgv_CellFormatting;
            dgvLiveMonitoring.CellFormatting += Dgv_CellFormatting;

            dgvCategorized.DataError += Dgv_DataError;
            dgvLiveMonitoring.DataError += Dgv_DataError;

            dgvLiveMonitoring.CellClick += DgvLiveMonitoring_CellClick;

            // ▼▼▼ [추가] Req 2. 자동 완성 지연 문제 해결 ▼▼▼
            dgvCategorized.CurrentCellDirtyStateChanged += Dgv_CurrentCellDirtyStateChanged;
            dgvLiveMonitoring.CurrentCellDirtyStateChanged += Dgv_CurrentCellDirtyStateChanged;
            // ▲▲▲ [추가] 완료 ▲▲▲

            RefreshPluginMetadataCache();
        }

        #region --- DataGridView 및 UI 초기화 ---

        private void InitializeDataGridViews()
        {
            // 탭 1: 분류 후 처리 (완료형)
            dgvCategorized.Columns.Clear();
            dgvCategorized.AutoGenerateColumns = false;
            dgvCategorized.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dgvCategorized.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "TaskName",
                HeaderText = Properties.Resources.UPLOAD_COL_TASKNAME, // "작업 이름"
                DataPropertyName = "TaskName",
                FillWeight = 15
            });
            dgvCategorized.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = "WatchFolder",
                HeaderText = Properties.Resources.UPLOAD_COL_CAT_FOLDER, // "감시 폴더 (RegEx 목적지)"
                DataPropertyName = "WatchFolder",
                FillWeight = 58
            });
            dgvCategorized.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = "PluginName",
                HeaderText = Properties.Resources.UPLOAD_COL_PLUGIN, // "실행 플러그인"
                DataPropertyName = "PluginName",
                FillWeight = 27
            });

            // 탭 2: 원본 직접 감시 (증분형)
            dgvLiveMonitoring.Columns.Clear();
            dgvLiveMonitoring.AutoGenerateColumns = false;
            dgvLiveMonitoring.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dgvLiveMonitoring.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "TaskName",
                HeaderText = Properties.Resources.UPLOAD_COL_TASKNAME, // "작업 이름"
                DataPropertyName = "TaskName",
                FillWeight = 16
            });
            dgvLiveMonitoring.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "WatchFolder",
                HeaderText = Properties.Resources.UPLOAD_COL_LIVE_FOLDER, // "원본 폴더"
                DataPropertyName = "WatchFolder",
                FillWeight = 35
            });
            dgvLiveMonitoring.Columns.Add(new DataGridViewButtonColumn
            {
                Name = "btnSelectFolder",
                HeaderText = Properties.Resources.UPLOAD_COL_SELECT, // "선택"
                Text = "...",
                UseColumnTextForButtonValue = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 5,
                Width = 40
            });
            dgvLiveMonitoring.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "FileFilter",
                HeaderText = Properties.Resources.UPLOAD_COL_FILTER, // "파일 필터"
                DataPropertyName = "FileFilter",
                FillWeight = 20
            });
            dgvLiveMonitoring.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = "PluginName",
                HeaderText = Properties.Resources.UPLOAD_COL_PLUGIN, // "실행 플러그인"
                DataPropertyName = "PluginName",
                FillWeight = 28
            });
        }

        private void InitializeComboBoxColumns()
        {
            var pluginNames = _pluginPanel.GetLoadedPlugins().Select(p => p.PluginName).ToArray();

            var dgvCatPluginCol = dgvCategorized.Columns["PluginName"] as DataGridViewComboBoxColumn;
            if (dgvCatPluginCol != null)
            {
                dgvCatPluginCol.Items.Clear();
                if (pluginNames.Length > 0) dgvCatPluginCol.Items.AddRange(pluginNames);
            }

            var regexFolders = _configPanel.GetRegexList().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string imageSaveFolder = _imageTransPanel.GetImageSaveFolder();
            if (!string.IsNullOrEmpty(imageSaveFolder) && !regexFolders.Contains(imageSaveFolder, StringComparer.OrdinalIgnoreCase))
            {
                regexFolders.Add(imageSaveFolder);
            }

            var dgvCatFolderCol = dgvCategorized.Columns["WatchFolder"] as DataGridViewComboBoxColumn;
            if (dgvCatFolderCol != null)
            {
                dgvCatFolderCol.Items.Clear();
                if (regexFolders.Count > 0) dgvCatFolderCol.Items.AddRange(regexFolders.ToArray());
            }

            var dgvLivePluginCol = dgvLiveMonitoring.Columns["PluginName"] as DataGridViewComboBoxColumn;
            if (dgvLivePluginCol != null)
            {
                dgvLivePluginCol.Items.Clear();
                if (pluginNames.Length > 0) dgvLivePluginCol.Items.AddRange(pluginNames);
            }
        }

        private void UcUploadPanel_Load(object sender, EventArgs e)
        {
            tpCategorized.Text = Properties.Resources.UPLOAD_TAB1_HEADER;
            tpLiveMonitoring.Text = Properties.Resources.UPLOAD_TAB2_HEADER;
            InitializeComboBoxColumns();
        }

        #endregion

        #region --- 설정 저장 / 로드 ---

        private void LoadSettings()
        {
            dgvCategorized.Rows.Clear();
            var tab1Settings = _settingsManager.GetFoldersFromSection(Tab1Section);
            foreach (string line in tab1Settings)
            {
                string[] parts = line.Split(new[] { "||" }, StringSplitOptions.None);
                if (parts.Length == 3)
                {
                    int rowIndex = dgvCategorized.Rows.Add();
                    dgvCategorized.Rows[rowIndex].Cells["TaskName"].Value = parts[0];
                    dgvCategorized.Rows[rowIndex].Cells["WatchFolder"].Value = parts[1];
                    dgvCategorized.Rows[rowIndex].Cells["PluginName"].Value = parts[2];
                }
            }

            dgvLiveMonitoring.Rows.Clear();
            var tab2Settings = _settingsManager.GetFoldersFromSection(Tab2Section);
            foreach (string line in tab2Settings)
            {
                string[] parts = line.Split(new[] { "||" }, StringSplitOptions.None);
                if (parts.Length == 4)
                {
                    int rowIndex = dgvLiveMonitoring.Rows.Add();
                    dgvLiveMonitoring.Rows[rowIndex].Cells["TaskName"].Value = parts[0];
                    dgvLiveMonitoring.Rows[rowIndex].Cells["WatchFolder"].Value = parts[1];
                    dgvLiveMonitoring.Rows[rowIndex].Cells["FileFilter"].Value = parts[2];
                    dgvLiveMonitoring.Rows[rowIndex].Cells["PluginName"].Value = parts[3];
                }
            }
        }

        private bool ValidateRules(DataGridView dgv, string tabName, out string errorMessage)
        {
            errorMessage = string.Empty;
            foreach (DataGridViewRow row in dgv.Rows)
            {
                if (row.IsNewRow) continue;
                string taskName = row.Cells["TaskName"].Value?.ToString();
                string pluginName = row.Cells["PluginName"].Value?.ToString();

                if (!string.IsNullOrEmpty(taskName) && taskName != "New Task")
                {
                    if (string.IsNullOrEmpty(pluginName) || pluginName == "(플러그인 선택)")
                    {
                        errorMessage = string.Format(Properties.Resources.MSG_RUN_PLUGIN_REQUIRED,
                                                        tabName, taskName);
                        return false;
                    }
                }
            }
            return true;
        }

        private void PerformSave(string section, DataGridView dgv)
        {
            var lines = new List<string>();
            foreach (DataGridViewRow row in dgv.Rows)
            {
                if (row.IsNewRow) continue;

                string taskName = row.Cells["TaskName"].Value?.ToString();
                string pluginName = row.Cells["PluginName"].Value?.ToString();

                bool isValid = !string.IsNullOrEmpty(taskName) && taskName != "New Task" &&
                               !string.IsNullOrEmpty(pluginName) && pluginName != "(플러그인 선택)";

                if (dgv == dgvCategorized && isValid)
                {
                    string watchFolder = row.Cells["WatchFolder"].Value?.ToString();
                    if (!string.IsNullOrEmpty(watchFolder) && watchFolder != "(폴더 선택)")
                        lines.Add(string.Join("||", taskName, watchFolder, pluginName));
                }
                else if (dgv == dgvLiveMonitoring && isValid)
                {
                    string watchFolder = row.Cells["WatchFolder"].Value?.ToString();
                    string fileFilter = row.Cells["FileFilter"].Value?.ToString();
                    // [수정] 원본 폴더가 비어있어도 저장 허용 (필요시) - 여기서는 비어있지 않도록 유지
                    if (!string.IsNullOrEmpty(watchFolder) && !string.IsNullOrEmpty(fileFilter))
                        lines.Add(string.Join("||", taskName, watchFolder, fileFilter, pluginName));
                }
            }
            _settingsManager.SetFoldersToSection(section, lines);
            _logManager.LogEvent($"[ucUploadPanel] Settings saved for section: {section}");
        }

        // 탭 1 버튼
        private void BtnCatAdd_Click(object sender, EventArgs e)
        {
            int rowIndex = dgvCategorized.Rows.Add();
            dgvCategorized.Rows[rowIndex].Cells["TaskName"].Value = "New Task";
            dgvCategorized.Rows[rowIndex].Cells["WatchFolder"].Value = null;
            dgvCategorized.Rows[rowIndex].Cells["PluginName"].Value = null;
        }

        private void BtnCatRemove_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow row in dgvCategorized.SelectedRows)
            {
                if (!row.IsNewRow) dgvCategorized.Rows.Remove(row);
            }
        }

        private void BtnCatSave_Click(object sender, EventArgs e)
        {
            string validationError;
            if (!ValidateRules(dgvCategorized, Properties.Resources.UPLOAD_TAB1_HEADER, out validationError))
            {
                MessageBox.Show(validationError, Properties.Resources.CAPTION_ERROR, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            PerformSave(Tab1Section, dgvCategorized);

            MessageBox.Show(Properties.Resources.MSG_SAVE_CAT_SUCCESS,
                            Properties.Resources.CAPTION_SAVE_COMPLETE, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 탭 2 버튼
        private void BtnLiveAdd_Click(object sender, EventArgs e)
        {
            int rowIndex = dgvLiveMonitoring.Rows.Add();
            dgvLiveMonitoring.Rows[rowIndex].Cells["TaskName"].Value = "New Task";

            // [수정] Req 1. 원본 폴더를 빈 값(null)으로 설정
            dgvLiveMonitoring.Rows[rowIndex].Cells["WatchFolder"].Value = null;

            dgvLiveMonitoring.Rows[rowIndex].Cells["FileFilter"].Value = "*.*";
            dgvLiveMonitoring.Rows[rowIndex].Cells["PluginName"].Value = null;
        }

        private void BtnLiveRemove_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow row in dgvLiveMonitoring.SelectedRows)
            {
                if (!row.IsNewRow) dgvLiveMonitoring.Rows.Remove(row);
            }
        }

        private void BtnLiveSave_Click(object sender, EventArgs e)
        {
            string validationError;
            if (!ValidateRules(dgvLiveMonitoring, Properties.Resources.UPLOAD_TAB2_HEADER, out validationError))
            {
                MessageBox.Show(validationError, Properties.Resources.CAPTION_ERROR, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            PerformSave(Tab2Section, dgvLiveMonitoring);

            MessageBox.Show(Properties.Resources.MSG_SAVE_LIVE_SUCCESS,
                            Properties.Resources.CAPTION_SAVE_COMPLETE, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        #endregion

        #region --- Watcher 시작 / 중지 (핵심 로직) ---

        public void UpdateStatusOnRun(bool isRunning)
        {
            SetControlsEnabled(!isRunning);

            if (isRunning)
            {
                StopWatchers();
                InitializeWatchers();
            }
            else
            {
                StopWatchers();
            }
        }

        private void InitializeWatchers()
        {
            _logManager.LogEvent("[ucUploadPanel] Initializing watchers...");

            InitializeComboBoxColumns();

            // 탭 1 (분류 후 완료형) 감시자 초기화
            lock (_lockTab1)
            {
                _tab1RuleMap.Clear();
                var foldersToWatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (DataGridViewRow row in dgvCategorized.Rows)
                {
                    if (row.IsNewRow) continue;
                    string folder = row.Cells["WatchFolder"].Value?.ToString();
                    string plugin = row.Cells["PluginName"].Value?.ToString();

                    if (string.IsNullOrEmpty(folder) || folder == "(폴더 선택)" ||
                        string.IsNullOrEmpty(plugin) || plugin == "(플러그인 선택)") continue;

                    foldersToWatch.Add(folder);
                    _tab1RuleMap[folder.ToUpperInvariant()] = plugin;
                }

                foreach (string folder in foldersToWatch)
                {
                    try
                    {
                        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                        var watcher = new FileSystemWatcher(folder)
                        {
                            Filter = "*.*",
                            IncludeSubdirectories = false,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                            EnableRaisingEvents = true
                        };
                        watcher.Created += OnTab1FileCreated;
                        _tab1Watchers.Add(watcher);
                        _logManager.LogEvent($"[ucUploadPanel] Tab1 Watcher (Categorized) started: {folder}");
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogError($"[ucUploadPanel] Failed to start Tab1 Watcher for {folder}: {ex.Message}");
                    }
                }
            }

            // 탭 2 (원본 증분형) 감시자 초기화
            lock (_lockTab2)
            {
                _tab2RuleMap.Clear();
                foreach (DataGridViewRow row in dgvLiveMonitoring.Rows)
                {
                    if (row.IsNewRow) continue;
                    string folder = row.Cells["WatchFolder"].Value?.ToString();
                    string filter = row.Cells["FileFilter"].Value?.ToString();
                    string plugin = row.Cells["PluginName"].Value?.ToString();

                    if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(filter) ||
                        string.IsNullOrEmpty(plugin) || plugin == "(플러그인 선택)") continue;

                    try
                    {
                        if (!Directory.Exists(folder))
                        {
                            _logManager.LogEvent($"[ucUploadPanel] Tab2 Watcher: Folder not found, skipping: {folder}");
                            continue;
                        }
                        var watcher = new FileSystemWatcher(folder)
                        {
                            Filter = filter,
                            IncludeSubdirectories = false,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                            EnableRaisingEvents = true
                        };
                        watcher.Changed += OnTab2FileChanged;
                        watcher.Created += OnTab2FileChanged;
                        _tab2Watchers.Add(watcher);

                        string mapKey = $"{folder.ToUpperInvariant()}|{filter.ToUpperInvariant()}";
                        _tab2RuleMap[mapKey] = plugin;

                        _logManager.LogEvent($"[ucUploadPanel] Tab2 Watcher (Live Log) started: {folder} | Filter: {filter}");
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogError($"[ucUploadPanel] Failed to start Tab2 Watcher for {folder} ({filter}): {ex.Message}");
                    }
                }
            }
        }

        private void StopWatchers()
        {
            lock (_lockTab1)
            {
                foreach (var watcher in _tab1Watchers)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Created -= OnTab1FileCreated;
                    watcher.Dispose();
                }
                _tab1Watchers.Clear();
                _tab1RuleMap.Clear();
            }
            lock (_lockTab2)
            {
                foreach (var watcher in _tab2Watchers)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Changed -= OnTab2FileChanged;
                    watcher.Created -= OnTab2FileChanged;
                    watcher.Dispose();
                }
                _tab2Watchers.Clear();
                _tab2RuleMap.Clear();
            }
            _logManager.LogEvent("[ucUploadPanel] All watchers stopped.");
        }

        #endregion

        #region --- 파일 이벤트 핸들러 및 플러그인 실행 ---

        // 탭 1 (완료형) 이벤트 핸들러
        private void OnTab1FileCreated(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType != WatcherChangeTypes.Created) return;
            Thread.Sleep(1000);

            // 1. 시간 기반 디바운스
            string fileKey = e.FullPath.ToUpperInvariant();
            DateTime now = DateTime.Now;

            if (_lastProcessEventTime.TryGetValue(fileKey, out var lastTime) && (now - lastTime).TotalSeconds < DebounceSeconds)
            {
                _logManager.LogDebug($"[ucUploadPanel] Debounce: Skipping event for {e.FullPath}, processed recently.");
                return;
            }
            _lastProcessEventTime[fileKey] = now;

            // 2. 규칙 찾기
            string folder = Path.GetDirectoryName(e.FullPath).ToUpperInvariant();
            string pluginName;

            if (!_tab1RuleMap.TryGetValue(folder, out pluginName))
            {
                var parentFolder = Directory.GetParent(folder)?.FullName.ToUpperInvariant();
                if (parentFolder == null || !_tab1RuleMap.TryGetValue(parentFolder, out pluginName))
                {
                    _logManager.LogDebug($"[ucUploadPanel] Tab1: No rule found for folder {folder}");
                    return;
                }
            }

            // [핵심 수정] Override 로직 및 플러그인 실행 흐름 제어
            string finalPath = e.FullPath;
            bool runPlugin = true; // 플러그인 실행 여부 플래그

            // 3. WaferFlat 플러그인일 경우, Override 로직을 "먼저" 수행
            if (_overridePanel != null && pluginName.Equals("Onto_WaferFlatData", StringComparison.OrdinalIgnoreCase))
            {
                _logManager.LogDebug($"[ucUploadPanel] Tab1: Running Override logic for {e.FullPath}");

                // [수정] 타임아웃을 10초(10000ms)로 늘려 안정성 확보
                string renamedPath = _overridePanel.EnsureOverrideAndReturnPath(e.FullPath, 10000);

                if (renamedPath != e.FullPath)
                {
                    // [성공] .info 파일 발견, 이름 변경 완료
                    finalPath = renamedPath;
                    _logManager.LogDebug($"[ucUploadPanel] Tab1: Override success. Processing renamed file: {finalPath}");
                }
                else
                {
                    // [실패] .info 파일을 찾지 못함 (타임아웃)
                    _logManager.LogError($"[ucUploadPanel] Tab1: Override FAILED. .info file not found for {e.FullPath}. Plugin execution will be SKIPPED.");
                    runPlugin = false; // ★ 플러그인 실행 차단
                }
            }

            // 4. [수정] runPlugin 플래그가 true일 때만 실행
            if (runPlugin)
            {
                _logManager.LogEvent($"[ucUploadPanel] Tab1 Processing (Categorized): {finalPath} -> Plugin: {pluginName}");
                RunPlugin(pluginName, finalPath);
            }
            else
            {
                // [추가] 스킵된 경우에도 디바운스 시간 갱신 (재처리 방지)
                _lastProcessEventTime[fileKey] = DateTime.Now;
                _logManager.LogDebug($"[ucUploadPanel] Skipped plugin execution for {e.FullPath}.");
            }
        }

        // 탭 2 (증분형) 이벤트 핸들러
        private void OnTab2FileChanged(object sender, FileSystemEventArgs e)
        {
            Thread.Sleep(200);

            // [수정] 시간 기반 디바운스 로직 (기존과 동일)
            string fileKey = e.FullPath.ToUpperInvariant();
            DateTime now = DateTime.Now;

            if (_lastProcessEventTime.TryGetValue(fileKey, out var lastTime) && (now - lastTime).TotalSeconds < DebounceSeconds)
            {
                _logManager.LogDebug($"[ucUploadPanel] Debounce: Skipping event for {e.FullPath}, processed recently.");
                return;
            }
            _lastProcessEventTime[fileKey] = now;

            string folder = Path.GetDirectoryName(e.FullPath).ToUpperInvariant();
            string filter = (sender as FileSystemWatcher)?.Filter.ToUpperInvariant();
            string mapKey = $"{folder}|{filter}";
            string pluginName;

            if (!_tab2RuleMap.TryGetValue(mapKey, out pluginName))
            {
                _logManager.LogDebug($"[ucUploadPanel] Tab2: No rule found for {mapKey}");
                return;
            }

            _logManager.LogEvent($"[ucUploadPanel] Tab2 Processing (Live Log): {e.FullPath} -> Plugin: {pluginName}");

            // [수정] CS0103 오류 해결: lockKey 파라미터 전달 제거
            RunPlugin(pluginName, e.FullPath);
        }

        private void RunPlugin(string pluginName, string filePath, string lockKey = null)
        {
            try
            {
                var pluginItem = _pluginPanel.GetLoadedPlugins()
                    .FirstOrDefault(p => p.PluginName.Equals(pluginName, StringComparison.OrdinalIgnoreCase));

                if (pluginItem == null || !File.Exists(pluginItem.AssemblyPath))
                {
                    _logManager.LogError($"[ucUploadPanel] Plugin DLL not found: {pluginName}");
                    return;
                }

                byte[] dllBytes = File.ReadAllBytes(pluginItem.AssemblyPath);
                Assembly asm = Assembly.Load(dllBytes);
                Type targetType = asm.GetTypes().FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.GetMethods().Any(m => m.Name == "ProcessAndUpload"));
                if (targetType == null)
                {
                    _logManager.LogError($"[ucUploadPanel] No class with ProcessAndUpload() method found in plugin: {pluginName}");
                    return;
                }

                object pluginObj = Activator.CreateInstance(targetType);

                MethodInfo mi = targetType.GetMethod("ProcessAndUpload", new[] { typeof(string), typeof(object), typeof(object) });

                object[] args;
                if (mi != null)
                {
                    args = new object[] { filePath, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.ini"), null };
                }
                else
                {
                    mi = targetType.GetMethod("ProcessAndUpload", new[] { typeof(string), typeof(string) });
                    if (mi != null)
                    {
                        args = new object[] { filePath, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.ini") };
                    }
                    else
                    {
                        mi = targetType.GetMethod("ProcessAndUpload", new[] { typeof(string) });
                        if (mi == null)
                        {
                            _logManager.LogError($"[ucUploadPanel] No compatible ProcessAndUpload() overload found in plugin: {pluginName}");
                            return;
                        }
                        args = new object[] { filePath };
                    }
                }

                Task.Run(() =>
                {
                    try
                    {
                        mi.Invoke(pluginObj, args);
                        _logManager.LogEvent($"[ucUploadPanel] Plugin execution completed: {pluginName} for {filePath}");
                    }
                    catch (Exception invokeEx)
                    {
                        _logManager.LogError($"[ucUploadPanel] Plugin execution failed: {pluginName}. Error: {invokeEx.GetBaseException().Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[ucUploadPanel] Failed to run plugin {pluginName}: {ex.Message}");
            }
        }

        #endregion

        #region --- 그리드 UI 헬퍼 ---

        private void Dgv_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.Value != null && e.Value != DBNull.Value) return;

            var dgv = sender as DataGridView;
            if (dgv == null) return;

            if (dgv.Columns[e.ColumnIndex] is DataGridViewComboBoxColumn)
            {
                string colName = dgv.Columns[e.ColumnIndex].Name;

                if (colName == "WatchFolder")
                {
                    e.Value = "(폴더 선택)";
                    e.FormattingApplied = true;
                }
                else if (colName == "PluginName")
                {
                    e.Value = "(플러그인 선택)";
                    e.FormattingApplied = true;
                }
            }
            // [추가] 탭 2의 원본 폴더 플레이스홀더
            else if (dgv == dgvLiveMonitoring && dgv.Columns[e.ColumnIndex].Name == "WatchFolder")
            {
                e.Value = "(경로 입력/선택)";
                e.FormattingApplied = true;
            }
        }

        private void Dgv_DataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            e.ThrowException = false;
        }

        private void DgvLiveMonitoring_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

            if (dgvLiveMonitoring.ReadOnly) return;

            if (dgvLiveMonitoring.Columns[e.ColumnIndex].Name == "btnSelectFolder")
            {
                using (var folderDialog = new FolderBrowserDialog())
                {
                    string currentPath = dgvLiveMonitoring.Rows[e.RowIndex].Cells["WatchFolder"].Value?.ToString();
                    if (!string.IsNullOrEmpty(currentPath) && currentPath != "(경로 입력/선택)" && Directory.Exists(currentPath))
                    {
                        folderDialog.SelectedPath = currentPath;
                    }
                    else
                    {
                        folderDialog.SelectedPath = _configPanel?.BaseFolderPath ?? AppDomain.CurrentDomain.BaseDirectory;
                    }

                    folderDialog.Description = "증분 감시할 원본 폴더를 선택하세요.";

                    if (folderDialog.ShowDialog() == DialogResult.OK)
                    {
                        dgvLiveMonitoring.Rows[e.RowIndex].Cells["WatchFolder"].Value = folderDialog.SelectedPath;
                    }
                }
            }
        }

        // [추가] Req 2. 콤보박스 선택 즉시 자동 완성
        private void Dgv_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            var dgv = sender as DataGridView;
            if (dgv != null && dgv.CurrentCell is DataGridViewComboBoxCell)
            {
                dgv.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void Dgv_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var dgv = sender as DataGridView;
            if (dgv == null) return;

            if (dgv.Columns[e.ColumnIndex].Name != "PluginName") return;

            string pluginName = dgv.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString();
            if (string.IsNullOrEmpty(pluginName) || pluginName == "(플러그인 선택)") return;

            (string Task, string Filter) metadata;
            if (!_pluginMetadataCache.TryGetValue(pluginName, out metadata)) return;

            var taskCell = dgv.Rows[e.RowIndex].Cells["TaskName"];
            string currentTaskName = taskCell.Value?.ToString();
            if (string.IsNullOrEmpty(currentTaskName) || currentTaskName == "New Task" || currentTaskName.EndsWith("(Auto)"))
            {
                taskCell.Value = metadata.Task;
            }

            if (dgv == dgvLiveMonitoring && metadata.Filter != null)
            {
                var filterCell = dgv.Rows[e.RowIndex].Cells["FileFilter"];
                string currentFilter = filterCell.Value?.ToString();
                if (string.IsNullOrEmpty(currentFilter) || currentFilter == "*.*")
                {
                    filterCell.Value = metadata.Filter;
                }
            }
        }
        #endregion

        #region --- Plugin Panel 연동 ---

        private void RefreshPluginMetadataCache()
        {
            _pluginMetadataCache.Clear();
            var pluginItems = _pluginPanel.GetLoadedPlugins();
            foreach (var item in pluginItems)
            {
                try
                {
                    (string Task, string Filter) metadata = LoadPluginMetadata(item.AssemblyPath);
                    if (metadata.Task != null)
                    {
                        _pluginMetadataCache[item.PluginName] = metadata;
                    }
                }
                catch (Exception ex) { _logManager.LogError($"Failed to load metadata for {item.PluginName}: {ex.Message}"); }
            }
        }

        private void OnPluginsChanged(object sender, EventArgs e)
        {
            InitializeComboBoxColumns();
            RefreshPluginMetadataCache();

            var validPluginNames = new HashSet<string>(
                _pluginPanel.GetLoadedPlugins().Select(p => p.PluginName),
                StringComparer.OrdinalIgnoreCase
            );

            int clearedCount = 0;

            // 탭 1 (완료형) 검사
            for (int i = dgvCategorized.Rows.Count - 1; i >= 0; i--)
            {
                var row = dgvCategorized.Rows[i];
                if (row.IsNewRow) continue;

                string selectedPlugin = row.Cells["PluginName"].Value?.ToString();

                if (!string.IsNullOrEmpty(selectedPlugin) &&
                    selectedPlugin != "(플러그인 선택)" &&
                    !validPluginNames.Contains(selectedPlugin))
                {
                    row.Cells["PluginName"].Value = null;
                    clearedCount++;
                    _logManager.LogEvent($"[ucUploadPanel] Tab1: Cleared plugin '{selectedPlugin}' from rule '{row.Cells["TaskName"].Value}' (plugin deleted).");
                }
            }

            // 탭 2 (증분형) 검사
            for (int i = dgvLiveMonitoring.Rows.Count - 1; i >= 0; i--)
            {
                var row = dgvLiveMonitoring.Rows[i];
                if (row.IsNewRow) continue;

                string selectedPlugin = row.Cells["PluginName"].Value?.ToString();

                if (!string.IsNullOrEmpty(selectedPlugin) &&
                    selectedPlugin != "(플러그인 선택)" &&
                    !validPluginNames.Contains(selectedPlugin))
                {
                    row.Cells["PluginName"].Value = null;
                    clearedCount++;
                    _logManager.LogEvent($"[ucUploadPanel] Tab2: Cleared plugin '{selectedPlugin}' from rule '{row.Cells["TaskName"].Value}' (plugin deleted).");
                }
            }

            // [수정] "조용한" 자동 저장 (알림창 X, 유효성 검사 X)
            if (clearedCount > 0)
            {
                // 알림창 없이 저장 로직만 호출
                PerformSave(Tab1Section, dgvCategorized);
                PerformSave(Tab2Section, dgvLiveMonitoring);

                string msg = string.Format(
                    Properties.Resources.MSG_PLUGIN_CLEARED_RULES,
                    clearedCount);

                // 알림은 띄움
                MessageBox.Show(
                    msg,
                    Properties.Resources.CAPTION_PLUGIN_CHANGED,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
        }

        private (string Task, string Filter) LoadPluginMetadata(string dllPath)
        {
            string taskName = null;
            string fileFilter = null;
            try
            {
                byte[] dllBytes = File.ReadAllBytes(dllPath);
                Assembly asm = Assembly.Load(dllBytes);

                var pluginType = asm.GetTypes().FirstOrDefault(t =>
                    t.IsClass && !t.IsAbstract &&
                    t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(m => m.Name == "ProcessAndUpload"));

                if (pluginType != null)
                {
                    object pluginObj = Activator.CreateInstance(pluginType);

                    var taskProp = pluginType.GetProperty("DefaultTaskName", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (taskProp != null && taskProp.PropertyType == typeof(string))
                        taskName = taskProp.GetValue(pluginObj, null) as string;

                    var filterProp = pluginType.GetProperty("DefaultFileFilter", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (filterProp != null && filterProp.PropertyType == typeof(string))
                        fileFilter = filterProp.GetValue(pluginObj, null) as string;
                }
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[ucUploadPanel] Failed to reflect metadata from {dllPath}: {ex.Message}");
            }
            return (taskName, fileFilter);
        }
        #endregion

        // --- 외부 호출 메서드 ---

        public void LoadImageSaveFolder_PathChanged()
        {
            InitializeComboBoxColumns();
            LoadSettings();
        }

        private void SetControlsEnabled(bool enabled)
        {
            dgvCategorized.ReadOnly = !enabled;
            btnCatAdd.Enabled = enabled;
            btnCatRemove.Enabled = enabled;
            btnCatSave.Enabled = enabled;

            dgvLiveMonitoring.ReadOnly = !enabled;
            btnLiveAdd.Enabled = enabled;
            btnLiveRemove.Enabled = enabled;
            btnLiveSave.Enabled = enabled;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopWatchers();
                if (components != null)
                {
                    components.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        public bool HasInvalidRules(out string errorMessage)
        {
            if (!ValidateRules(dgvCategorized, Properties.Resources.UPLOAD_TAB1_HEADER, out errorMessage))
            {
                return true;
            }

            if (!ValidateRules(dgvLiveMonitoring, Properties.Resources.UPLOAD_TAB2_HEADER, out errorMessage))
            {
                return true;
            }

            errorMessage = string.Empty;
            return false;
        }
    }
}
