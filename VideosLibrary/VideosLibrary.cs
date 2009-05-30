﻿using System;
using System.IO;
using System.Xml;
using System.Data;
using System.Linq;
using System.Drawing;
using System.Threading;
using System.Collections;

using System.Data.Common;
using System.Windows.Forms;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using GBPVR.Public;
using GBPVRX2;
using GBPVRX2.UiSupport;


/*  #References(.NET v2.0 w/LinqBridge)#
 *  System
 *  System.Xml
 *  System.Core
 *  System.Data
 *  System.Drawing
 *  System.Windows.Forms
 *  GBPVRPublic
 *  PVRUiPublic
 *  
 *  COM: Windows Script Host Object Model (IWshRuntimeLibrary)
 */


namespace VideosLibraryPlugin
{
    using ViewMode = UiList.ViewMode;

    public class VideosLibrary : BaseButtonListUiTask, IUiPopupCallback, SkinHelper2.GetImageCallback
    {
        protected const int IMG_CACHE_LEN = 50;
        //protected const int IMG_COMPRESSION = 35;
        protected const float RELOAD_TIMEOUT = 180;
        protected const float WATCHED_PADDING = 10;
        protected const string UP_ENTRY_KEY = "upEntry";
        protected const string IMG_PATH_KEY = "imagePath";
        protected const string RERENDER_KEY = "FORCE_RERENDER";
        protected const string REG_VIEW_NAME = "VideoLibraryView";
        protected const string REG_SORT_NAME = "VideoLibrarySort";
        protected const string REG_KEY = @"HKEY_CURRENT_USER\Software\devnz";
        protected const string XML_XPATH = @"/settings/PluginSettings/VideosLibrary/";

        protected bool confirmDelete;// = true;
        protected bool hybridViewMode;// = false;
        protected bool bracketDirNames;// = true;
        protected bool onlyDirsWithVids;// = false;
        protected bool strictFolderMetadata;// = false;
        protected bool showPlay, showMode, showSort, showPlayAll, showDelete, showMainMenu;

        protected bool activated = false;
        protected bool viewModeModified = false;
        protected bool ignoreNextSelect = false; //hack.
        protected volatile bool doNeedRendering = false;

        protected string upImgPath = null;
        protected string fileImgPath = null;
        protected string folderImgPath = null;

        protected GBPVRUiElement statusUi = null;
        protected GBPVRUiElement summaryUi = null;
        protected GBPVRUiElement coverUi = null;

        protected List<Bitmap> imgCache = null;
        protected List<ImageInfo> imgRequests = null;
        protected Dictionary<string, int> imgIds = null;
        protected Dictionary<ImageType, string> imgTypes = null;

        protected EntryModel entryModel = null;
        protected IPluginHelper pluginHelper = null;
        protected KeyCommandHelper keyHelper = null;
        protected FileSystemWatcher fileWatcher = null;
        protected static DbConnection dbConnection = null; //temp static
        protected Dictionary<string, bool> options = null;
        protected Dictionary<ViewMode, int> viewCounts = null;
        protected List<KeyValuePair<Command, string>> commands = null;

        protected enum EntryType { NULL, UP, DVD, FOLDER, FILE }
        protected enum SortMethod { ALPHA_NUMERIC, CREATION, SHUFFLE }
        protected enum Playback { NULL, UNWATCHED, WATCHING, FINISHED }
        protected enum ImageType { NULL, PREVIEW, FILE_IMAGE, FOLDER_IMAGE }
        protected enum Command { PLAY, MODE, SORT, PLAY_ALL, DELETE, MAIN_MENU }


        

        //TEMP IMPL
        protected Dictionary<string, Playback> playbackCache = new Dictionary<string, Playback>();
        static protected string picExtensions = @"^.+\.(bmp|jpg|png|tiff)$";





        #region "Subclasses"
        //#########################################################################################
        //#########################################################################################


        /// <summary>
        /// Helper used for generic type inference.
        /// </summary>
        protected static Triple<T1, T2, T3> MakeTriple<T1, T2, T3>(T1 first, T2 second, T3 third)
        {
            return new Triple<T1, T2, T3>(first, second, third);
        }


        /// <summary>
        /// Popup subclass with delete semantics.
        /// </summary>
        protected class DeletePopup : GBPVRX2.Popups.PopupMessageBox
        {
            public int Index { get; set; }
            public DeletePopup(IUiPopupCallback callback, string msg, int deleteIndex)
                : base(callback, msg, "OK", "Cancel")
            {
                Index = deleteIndex;
                base.OnKeyDown(new KeyEventArgs(Keys.Right));
            }
        }


        /// <summary>
        /// Represents a single file or folder entry.
        /// </summary>
        protected class Entry
        {
            public Entry()
            {
                Name = "";
                IsLink = false;
                Type = EntryType.NULL; 
                Status = Playback.NULL;
                Created = DateTime.MinValue;
                NeedsRefreshing = true;
            }
            public string Name { get; set; }
            public bool IsLink { get; set; }
            public EntryType Type { get; set; }
            public Playback Status { get; set; }
            public DateTime Created { get; set; }
            public bool NeedsRefreshing { get; set; }
        }


        /// <summary>
        /// Represents an image; x and y will be either position or size values.
        /// The path list is all search paths that result in this image.
        /// </summary>
        protected struct ImageInfo
        {
            public List<string> Paths;
            public string ImgPath;
            public ImageType Type;
            public int ValX;
            public int ValY;

            public ImageInfo(string path, string imgPath, ImageType type, int valX, int valY)
            {
                Paths = new string[] { path }.ToList();
                ImgPath = imgPath;
                Type = type;
                ValX = valX;
                ValY = valY;
            }
        }


        /// <summary>
        /// Generic three-element tuple.
        /// </summary>
        protected class Triple<T1, T2, T3>
        {
            public T1 First { get; set; }
            public T2 Second { get; set; }
            public T3 Third { get; set; }

            public Triple(T1 first, T2 second, T3 third)
            {
                First = first;
                Second = second;
                Third = third;
            }
        }


        /// <summary>
        /// Backs the current file system view.
        /// </summary>
        protected class EntryModel : List<KeyValuePair<string, Entry>>
        {
            public int BottomIndex { get; set; }
            public int CurrentIndex { get; set; }
            public Entry CurrentEntry { get; set; }
            public string CurrentPath { get; set; }
            public string FileExtensions { get; set; }
            public bool ShowExtensions { get; set; }
            public bool NeedsRefreshing { get; set; }
            public bool NeedsReloading { get; set; }
            public DateTime LastReload { get; set; }
            public DateTime LastManualSort { get; set; }
            public Stack<string> FolderStack { get; set; }
            public Stack<Triple<int, int, string>> IndexStack { get; set; }
            public List<KeyValuePair<string, string>> TopDirs { get; set; }

            public SortMethod Sorting { get; set; }
            public bool ReverseCreationSort { get; set; }
            public bool ReverseAlphaNumericSort { get; set; }

            public EntryModel()
            {
                BottomIndex = 0;
                CurrentIndex = 0;
                CurrentEntry = null;
                CurrentPath = "";
                FileExtensions = "";
                ShowExtensions = true;
                NeedsRefreshing = true;
                NeedsReloading = true;
                LastReload = DateTime.Now;
                LastManualSort = DateTime.Now;
                FolderStack = new Stack<string>();
                IndexStack = new Stack<Triple<int, int, string>>();
                TopDirs = new List<KeyValuePair<string, string>>();

                Sorting = SortMethod.ALPHA_NUMERIC;
                ReverseCreationSort = false;
                ReverseAlphaNumericSort = false;
            }

            public bool AtTopDir()
            {
                return (FolderStack.Count == 0);
            }

            public bool AtTopConfigDir()
            {
                return (AtTopDir() && TopDirs.Count > 1);
            }

            public int GetFolderCount()
            {
                return this.Count(pair =>
                    pair.Value.Type == EntryType.UP ||
                    pair.Value.Type == EntryType.FOLDER);
            }

            public string GetDirectory()
            {
                return (FolderStack.Count == 0)
                    ? TopDirs.First().Key : Directory.GetCurrentDirectory();
            }

            public void SetDirectory(string cwd)
            {
                Directory.SetCurrentDirectory(cwd);
            }

            public void PushLevel(string path)
            {
                if (Sorting == SortMethod.SHUFFLE)
                {
                    Sorting = SortMethod.ALPHA_NUMERIC;
                    LastManualSort = DateTime.Now;
                }
                IndexStack.Push(MakeTriple(BottomIndex, CurrentIndex, CurrentPath));
                FolderStack.Push(GetDirectory());
                SetDirectory(path);
                BottomIndex = 1;
                CurrentIndex = 1;
                CurrentPath = "";
            }

            public void PopLevel()
            {
                if (Sorting == SortMethod.SHUFFLE)
                {
                    Sorting = SortMethod.ALPHA_NUMERIC;
                    LastManualSort = DateTime.Now;
                }
                var triple = IndexStack.Pop();
                BottomIndex = triple.First;
                CurrentIndex = triple.Second;
                CurrentPath = triple.Third;
                SetDirectory(FolderStack.Pop());
            }

            public Entry this[string path]
            {
                get { return this.First(pair => pair.Key.Equals(path)).Value; }
                set { this[IndexOf(path)] = MakePair(path, value); }
            }

            public bool Add(string path, Entry entry)
            {
                bool status = !this.Any(pair => Path.GetFileName(pair.Key).Equals(entry.Name));
                if (status) this.Add(MakePair(path, entry));
                return status;
            }

            public bool Insert(int index, string path, Entry entry)
            {
                bool status = !this.Any(pair => pair.Value.Name.Equals(entry.Name));
                if (status) this.Insert(index, MakePair(path, entry));
                return status;
            }

            public bool Remove(string path)
            {
                bool exists = Contains(path);
                if (exists) this.RemoveAt(IndexOf(path));
                return exists;
                    
            }

            public bool Contains(string path)
            {
                return this.Any(pair => pair.Key.Equals(path));
            }

            public int IndexOf(string path)
            {
                return this.FindIndex(pair => pair.Key.Equals(path));
            }

            public new void Sort()
            {
                Sort(Sorting);
            }

            public void Sort(SortMethod sorting)
            {
                if (AtTopConfigDir())
                    return;
                else if (sorting == SortMethod.ALPHA_NUMERIC)
                    Sort(this, ((a, b) => (ReverseAlphaNumericSort ? -1 : 1) *
                        Path.GetFileName(a.Key).CompareTo(Path.GetFileName(b.Key))));
                else if (sorting == SortMethod.CREATION)
                    Sort(this, ((a, b) => (ReverseCreationSort ? 1 : -1) *
                        a.Value.Created.CompareTo(b.Value.Created)));
                else if (sorting == SortMethod.SHUFFLE)
                {
                    Sort(SortMethod.ALPHA_NUMERIC);
                    Shuffle(this);
                }
            }

            protected static void Sort(EntryModel model, Comparison<KeyValuePair<string, Entry>> cmp)
            {
                int skip = (model.AtTopDir() ? 0 : 1);
                int dirCount = model.GetFolderCount();
                var dirs = model.GetRange(skip, dirCount - skip);
                var files = model.GetRange(dirCount, model.Count - dirCount);
                model.RemoveRange(skip, model.Count - skip);
                dirs.Sort(cmp);
                files.Sort(cmp);
                model.AddRange(dirs);
                model.AddRange(files);
            }

            protected static void Shuffle(EntryModel model)
            {
                int dirCount = model.GetFolderCount();
                if (model.Count - dirCount > 1)
                {
                    Random rand = new Random();
                    for (int idx = dirCount; idx < model.Count - 1; ++idx)
                    {
                        int swapIdx = rand.Next(model.Count - idx) + idx;
                        var tmp = model[idx];
                        model[idx] = model[swapIdx];
                        model[swapIdx] = tmp;
                    }
                }
            }
        }


        /// <summary>
        /// A settings form for display in the config application.
        /// </summary>
        class VideosLibraryForm : Form
        {
            public XmlDocument Config { get; set; }

            public VideosLibraryForm(VideosLibrary plugin, XmlDocument config)
            {
                int xPos = 10, yPos = 0;
                var options = plugin.InitOptions((Config = config));

                base.AutoSize = true;
                base.Text = plugin.getName();
                base.Controls.Add(new Label() { Text = "* Optional Settings", AutoSize = true });
                foreach (var key in options.Keys)
                {
                    var loc = new Point(xPos, yPos += 20);
                    var state = (options[key] ? CheckState.Checked : CheckState.Unchecked);
                    base.Controls.Add(new CheckBox() { Text = key, CheckState = state, Location = loc, AutoSize = true });
                }
                var okay = new Button() { Text = "OK", Location = new Point(xPos, yPos += 40) };
                var cancel = new Button() { Text = "Cancel", Location = new Point(xPos + 80, yPos) };
                okay.Click += new EventHandler(Exit);
                cancel.Click += new EventHandler(Exit);
                base.Controls.Add(okay);
                base.Controls.Add(cancel);
            }

            protected void Exit(object sender, EventArgs e)
            {
                if (((Button)sender).Text.Equals("OK"))
                {
                    foreach (var control in base.Controls)
                    {
                        if (!(control is CheckBox)) continue;
                        CheckBox box = (CheckBox)control;
                        SetSingle(Config, XML_XPATH + box.Text, (box.CheckState == CheckState.Checked));
                    }
                }
                base.Close();
            }
        }


        #endregion "Subclasses"


        #region "Utilities"
        //#########################################################################################
        //#########################################################################################


        /// <summary>
        /// Helper used to retrieve an xml attribute value.
        /// </summary>
        protected static T GetAttribute<T>(XmlNode node, string xpath, T fallback)
        {
            bool isValidNode = (node != null && node.Attributes != null);
            return (isValidNode && (node = node.Attributes.GetNamedItem(xpath)) != null)
                ? (T)Convert.ChangeType(node.InnerText, typeof(T)) : fallback;
        }


        /// <summary>
        /// Helper used to retrieve an xml tag value.
        /// </summary>
        protected static T GetSingle<T>(XmlNode node, string xpath, T fallback)
        {
            node = node.SelectSingleNode(xpath);
            return (node != null ? (T)Convert.ChangeType(node.InnerText, typeof(T)) : fallback);
        }


        /// <summary>
        /// Helper used to set an xml tag value.
        /// </summary>
        protected static XmlNode SetSingle<T>(XmlNode node, string xpath, T val)
        {
            xpath = xpath.Trim('/') + '/';
            int nameLen = xpath.IndexOf('/');
            string name = xpath.Substring(0, nameLen);
            xpath = xpath.Substring(nameLen);

            var doc = node.OwnerDocument;
            node = node.SelectSingleNode(name)
                ?? node.AppendChild(doc.CreateElement(name));
            if (xpath.Length <= 1) node.InnerText = val.ToString();
            else return SetSingle(node, xpath, val);

            return node;
        }


        /// <summary>
        /// Helper used for generic type inference.
        /// </summary>
        protected static KeyValuePair<T1, T2> MakePair<T1, T2>(T1 key, T2 value)
        {
            return new KeyValuePair<T1, T2>(key, value);
        }


        /// <summary>
        /// Retrieves a column value from the specified table and row.
        /// </summary>
        protected static object GetDbValue(string colKey, string table, string row)
        {
            if (dbConnection.State != ConnectionState.Open)
                return DBNull.Value;
            DbCommand command = dbConnection.CreateCommand();
            command.CommandText = "SELECT " + colKey + " FROM " + table + " WHERE " + row;
            return command.ExecuteScalar();
        }


        /// <summary>
        /// Do a database lookup for the playback status associated with this path.
        /// </summary>
        protected static Playback GetPlaybackStatus(string path)
        {
            string table = "PLAYBACK_POSITION";
            string row = "filename=\"" + path + "\"";
            object posObj = GetDbValue("last_position", table, row);
            object endObj = GetDbValue("duration", table, row);
            int pos = (posObj is DBNull ? 0 : Convert.ToInt32(posObj));
            int end = (endObj is DBNull ? -1 : Convert.ToInt32(endObj));

            float pad = WATCHED_PADDING;
            if (end >= pos && end - pos <= pad) return Playback.FINISHED;
            else if (pos >= pad) return Playback.WATCHING;
            else return Playback.UNWATCHED;
        }


        /// <summary>
        /// Return a textStyle string based on the view mode and playback position.
        /// </summary>
        protected static string GetTextStyle(string path, Playback status, UiList.ViewMode mode)
        {
            string textStyle =
                (status == Playback.WATCHING) ? "WatchingTextStyle" :
                (status == Playback.FINISHED) ? "FinishedTextStyle" :
                (mode == UiList.ViewMode.MODE_LIST) ? "ListViewItems" :
                (mode == UiList.ViewMode.MODE_DETAILS) ? "DetailsViewItemsTitle" :
                (mode >= UiList.ViewMode.MODE_ICON) ? "IconViewItems" : "GeneralTextStyle";
            return textStyle;
        }


        /// <summary>
        /// Return the max number of viewable elements for each view mode.
        /// </summary>
        protected static Dictionary<UiList.ViewMode, int> GetViewableCounts(SkinHelper2 helper)
        {
            var counts = new Dictionary<UiList.ViewMode, int>();
            var node = helper.getPlacementNode("ListView");
            counts[UiList.ViewMode.MODE_LIST] = GetAttribute(node, "rows", 0);

            node = helper.getPlacementNode("DetailsView");
            counts[UiList.ViewMode.MODE_DETAILS] = GetAttribute(node, "rows", 0);

            node = helper.getPlacementNode("FilmstripView");
            counts[UiList.ViewMode.MODE_FILMSTRIP] = GetAttribute(node, "columns", 0);

            node = helper.getPlacementNode("IconView");
            int rows = GetAttribute(node, "rows", 0);
            int cols = GetAttribute(node, "columns", 0);
            counts[UiList.ViewMode.MODE_ICON] = rows * cols;

            return counts;
        }


        /// <summary>
        /// Simplifies registry queries.
        /// </summary>
        protected static string GetRegData(string valueName, object defaultVal)
        {
            return Microsoft.Win32.Registry.GetValue(REG_KEY, valueName, defaultVal).ToString();
        }


        /// <summary>
        /// Simplifies registry queries.
        /// </summary>
        protected static void SetRegData(string valueName, object val)
        {
            Microsoft.Win32.Registry.SetValue(REG_KEY, valueName, val);
        }


        /// <summary>
        /// Make sure the path is a valid directory (w/files if specified by config).
        /// </summary>
        protected static bool IsValidDir(string path, string fileRegex, bool requireVids)
        {
            var info = new DirectoryInfo(path);
            bool isDir = ((info.Attributes & FileAttributes.Directory) != 0);
            bool isHidden = ((info.Attributes & FileAttributes.Hidden) != 0);
            bool isSystem = ((info.Attributes & FileAttributes.System) != 0);
            if (!isDir || isHidden || isSystem) return false;
            else if (!requireVids) return true;

            FileInfo[] files = null;
            try { files = info.GetFiles(); }
            catch (Exception e) { Logger.Verbose(e.Message); }
            if (files == null) return false;

            foreach (var file in files)
                if (IsValidFile(file.FullName, fileRegex)) return true;
            foreach (var dir in info.GetDirectories())
                if (IsValidDir(dir.FullName, fileRegex, requireVids)) return true;
            return false;
        }


        /// <summary>
        /// Make sure the path is a valid file as specified by config.
        /// </summary>
        protected static bool IsValidFile(string path, string fileRegex)
        {
            var info = new FileInfo(path);
            bool isFile = File.Exists(path);
            bool isHidden = ((info.Attributes & FileAttributes.Hidden) != 0);
            bool isMatch = Regex.IsMatch(path, fileRegex, RegexOptions.IgnoreCase);
            return (isFile && !isHidden && isMatch);
        }


        /// <summary>
        /// Wrapper for the individual dir and file functions.
        /// </summary>
        protected static bool IsValidDirOrFile(string path, string fileRegex, bool requireVids)
        {
            return (Directory.Exists(path)
                ? IsValidDir(path, fileRegex, requireVids)
                : IsValidFile(path, fileRegex));
        }


        /// <summary>
        /// Find the first file starting with a prefix and matching the regex.
        /// </summary>
        protected static string FindFile(string dir, string filePrefix, string fileRegex)
        {
            string path = null;
            foreach (var file in new DirectoryInfo(dir).GetFiles(filePrefix + "*"))
            {
                if (Regex.IsMatch(file.FullName, fileRegex))
                {
                    path = file.FullName;
                    break;
                }
            }
            return path;
        }


        /// <summary>
        /// Find an xml file for the given video file or directory.
        /// If strict, only allow folder.xml for directories.
        /// </summary>
        protected static string FindXmlFile(string path, string fileRegex, bool strict)
        {
            string xmlPath = null;
            string dvdExt = ".dvd_";
            if (Directory.Exists(path) && Path.GetFileName(path).Equals("VIDEO_TS"))
            {
                path = Path.GetDirectoryName(path);
                path = Path.Combine(path, Path.GetFileName(path)) + dvdExt;
            }

            bool isFile = (File.Exists(path) || path.EndsWith(dvdExt));
            if (isFile)
            {
                xmlPath = Path.ChangeExtension(path, ".xml");
                path = Path.GetDirectoryName(path);
            }
            else
            {
                string dvdPath = Path.Combine(path, "VIDEO_TS");
                if (Directory.Exists(dvdPath))
                    return FindXmlFile(dvdPath, fileRegex, strict);

                xmlPath = Path.Combine(path, "Folder.xml");
                if (!File.Exists(xmlPath) && !strict)
                {
                    foreach (var file in new DirectoryInfo(path).GetFiles())
                    {
                        if (IsValidFile(file.FullName, fileRegex))
                        {
                            xmlPath = Path.ChangeExtension(file.FullName, "xml");
                            if (!File.Exists(xmlPath)) xmlPath = null;
                            else break;
                        }
                    }
                }
            }

            if (!File.Exists(xmlPath) && ((isFile || !strict)))
                xmlPath = Path.Combine(path, "MyMovies.xml");
            return (File.Exists(xmlPath) ? xmlPath : null);
        }


        /// <summary>
        /// Find cover art for a given video file or directory.
        /// If strict, only allow folder.jpg (or a metadata-specific image) for directories.
        /// </summary>
        protected static string FindCoverArt(string path, string fileRegex, bool strict)
        {
            string imgPath = null;
            string dvdExt = ".dvd_";
            if (Directory.Exists(path) && Path.GetFileName(path).Equals("VIDEO_TS"))
            {
                path = Path.GetDirectoryName(path);
                path = Path.Combine(path, Path.GetFileName(path)) + dvdExt;
            }

            var metadata = new Hashtable();
            string xmlPath = FindXmlFile(path, fileRegex, strict);
            bool isFile = (File.Exists(path) || path.EndsWith(dvdExt));
            if (GetXmlMetadata(metadata, xmlPath))
            {
                string dir = (isFile ? Path.GetDirectoryName(path) : path);
                imgPath = metadata[IMG_PATH_KEY].ToString();
                if (!File.Exists(imgPath))
                    imgPath = Path.Combine(dir, imgPath);
                if (File.Exists(imgPath))
                    return imgPath;
            }

            if (isFile)
            {
                string dir = Path.GetDirectoryName(path);
                string prefix = Path.GetFileNameWithoutExtension(path);
                imgPath = FindFile(dir, prefix, picExtensions);
            }
            else
            {
                string dvdPath = Path.Combine(path, "VIDEO_TS");
                if (Directory.Exists(dvdPath))
                    return FindCoverArt(dvdPath, fileRegex, strict);

                imgPath = FindFile(path, "Folder", picExtensions);
                if (!File.Exists(imgPath) && !strict)
                {
                    foreach (var file in new DirectoryInfo(path).GetFiles())
                    {
                        if (IsValidFile(file.FullName, fileRegex))
                        {
                            string prefix = Path.GetFileNameWithoutExtension(file.FullName);
                            imgPath = FindFile(path, prefix, picExtensions);
                            if (!File.Exists(imgPath)) imgPath = null;
                            else break;
                        }
                    }
                }
            }
            return (File.Exists(imgPath) ? imgPath : null);
        }


        /// <summary>
        /// Return known tag|metadata pairs from the provided xml file.
        /// </summary>
        protected static bool GetXmlMetadata(Hashtable metadata, string xmlPath)
        {
            if (File.Exists(xmlPath))
            {
                var doc = new XmlDocument();
                try { doc.Load(xmlPath); }
                catch (Exception e) { Logger.Error(e.Message); }
                metadata["@genre"] = GetSingle(doc, "/Title/Genres", "");
                metadata["@title"] = GetSingle(doc, "/Title/LocalTitle", "");
                metadata["@duration"] = GetSingle(doc, "/Title/RunningTime", "");
                metadata["@description"] = GetSingle(doc, "/Title/Description", "");
                metadata[IMG_PATH_KEY] = GetSingle(doc, "/Title/Covers/Front", "");
                return true;
            }
            return false;
        }


        #endregion "Utilities"


        #region "Helpers
        //#########################################################################################
        //#########################################################################################


        /// <summary>
        /// Initialize options from a config file, then provide a map of said options.
        /// </summary>
        protected Dictionary<string, bool> InitOptions(XmlDocument config)
        {
            var dict = new Dictionary<string, bool>();
            if (entryModel == null) entryModel = new EntryModel();

            //Retrieve default gbpvr settings.
            string key, dirStr, prefix = "/settings/";
            showDelete = GetSingle(config, prefix + "ShowDeleteInVideoLibrary", true);
            confirmDelete = GetSingle(config, prefix + "ConfirmDeleteInVideoLibrary", true);
            entryModel.FileExtensions = GetSingle(config, prefix + "VideoLibraryExtensions", ".*");
            dirStr = GetSingle(config, prefix + "VideoLibraryDirectory", @"C:\");

            //Retrieve plugin-specific settings.
            prefix = XML_XPATH;
            entryModel.ReverseCreationSort = dict[(key = "ReverseCreationSort")] = GetSingle(config, prefix + key, false);
            entryModel.ReverseAlphaNumericSort = dict[(key = "ReverseAlphaNumericSort")] = GetSingle(config, prefix + key, false);
            entryModel.ShowExtensions = dict[(key = "ViewExtensions")] = GetSingle(config, prefix + key, true);
            confirmDelete = dict[(key = "ConfirmDelete")] = GetSingle(config, prefix + key, confirmDelete);
            onlyDirsWithVids = dict[(key = "OnlyDirsWithVids")] = GetSingle(config, prefix + key, false);
            strictFolderMetadata = dict[(key = "StrictFolderMetadata")] = GetSingle(config, prefix + key, false);
            bracketDirNames = dict[(key = "UseBrackets")] = GetSingle(config, prefix + key, true);
            hybridViewMode = dict[(key = "HybridView")] = GetSingle(config, prefix + key, false);
            showPlay = dict[(key = "ShowPlay")] = GetSingle(config, prefix + key, false);
            showMode = dict[(key = "ShowMode")] = GetSingle(config, prefix + key, true);
            showSort = dict[(key = "ShowSort")] = GetSingle(config, prefix + key, true);
            showPlayAll = dict[(key = "ShowPlayAll")] = GetSingle(config, prefix + key, true);
            showDelete = dict[(key = "ShowDelete")] = GetSingle(config, prefix + key, showDelete);
            showMainMenu = dict[(key = "ShowMainMenu")] = GetSingle(config, prefix + key, true);

            //Plugin settings that may override gbpvr settings.
            string[] dirs = GetSingle(config, prefix + "VideoLibraryDirectory", dirStr).Split('|');
            for (int i = 0; i < dirs.Length - 1 && dirs[i].Length > 0; ++i)
            {
                int pathIdx = dirs[i].IndexOf('~');
                string name = dirs[i].Substring(0, pathIdx);
                string path = dirs[i].Substring(pathIdx + 1);
                entryModel.TopDirs.Add(MakePair(path, name));
            }

            return dict;
        }


        /// <summary>
        /// Recursively watch a path for file system events.
        /// </summary>
        protected void WatcherInit(string path)
        {
            Logger.Verbose("VideosLibrary::WatcherInit; " + path);
            fileWatcher = new FileSystemWatcher(path);
            fileWatcher.Created += WatcherAction;
            fileWatcher.Deleted += WatcherAction;
            fileWatcher.Renamed += WatcherAction;
            fileWatcher.Error += WatcherError;
            fileWatcher.IncludeSubdirectories = false;
            fileWatcher.EnableRaisingEvents = true;
        }


        /// <summary>
        /// Action to perform if a change is detected while watching the file system.
        /// </summary>
        protected void WatcherAction(Object sender, FileSystemEventArgs e)
        {
            Logger.Verbose("VideosLibrary::WatcherAction; " + e.ChangeType + ", " + e.FullPath);
            if (false && e.ChangeType == WatcherChangeTypes.Deleted)
            {
                //possibly already deleted if done in gui... ///////////////////////////////////////matching locks elsewhere?
                lock (entryModel) entryModel.Remove(e.FullPath);
                entryModel.NeedsRefreshing = true;
            }
            else entryModel.NeedsReloading = true;
        }


        /// <summary>
        /// Action to perform if an error is encountered while watching the file system.
        /// </summary>
        protected void WatcherError(Object sender, ErrorEventArgs e)
        {
            fileWatcher = new FileSystemWatcher();
            while (!fileWatcher.EnableRaisingEvents)
            {
                try { WatcherInit(entryModel.CurrentPath); }
                catch { System.Threading.Thread.Sleep(5000); }
            }
            entryModel.NeedsReloading = true;
            Logger.Verbose("VideosLibrary::WatcherError; " + e.GetException());
        }


        /// <summary>
        /// Assign a new ui element if null and update it based on the current skin.
        /// </summary>
        protected void AddUiElement(ArrayList list, Hashtable parameters, ref GBPVRUiElement ui)
        {
            string name = Convert.ToString(parameters["name"]);
            decimal alpha = (parameters.ContainsKey("alpha"))
                ? Convert.ToDecimal(parameters["alpha"]) : 255;
            bool hasRect = skinHelper2.checkPlacementRectDefined(name);
            bool hasImg = skinHelper2.checkCompositeImageDefined(name);

            //CHECK SKIN FOR INSET FLAG?

            if (hasRect && hasImg)
            {
                var img = skinHelper2.getNamedImage(name, parameters);
                var rect = skinHelper2.getPlacementRect(name);
                if (ui != null) ui.image = img;
                else ui = new GBPVRUiElement(name, rect, img);
                bool render = (alpha != ui.alpha);
                doNeedRendering |= render;
                ui.forceRefresh = true;
                ui.alpha = (int)alpha;
                list.Add(ui);
            }
            parameters.Clear();
        }


        /// <summary>
        /// Hybrid view uses list view if there are files and icon view otherwise.
        /// </summary>
        protected UiList.ViewMode UpdatedViewMode()
        {
            bool onlyFolders = (entryModel.GetFolderCount() == entryModel.Count);
            if (hybridViewMode && !viewModeModified && onlyFolders)
                return UiList.ViewMode.MODE_ICON;
            else if (hybridViewMode && !viewModeModified && !onlyFolders)
                return UiList.ViewMode.MODE_LIST;
            else
            {
                string viewStr = "MODE_" + GetRegData(REG_VIEW_NAME, "LIST");
                var mode = (UiList.ViewMode)Enum.Parse(typeof(UiList.ViewMode), viewStr);
                viewModeModified = (onlyFolders && mode != ViewMode.MODE_ICON);
                viewModeModified |= (!onlyFolders && mode != ViewMode.MODE_LIST);
                return mode;
            }
        }


        /// <summary>
        /// Constructs a list from the entryModel that's assignable to uiList.
        /// </summary>
        protected ArrayList UpdatedUiList()
        {
            var itemList = new ArrayList();
            var mode = base.uiList.getViewMode();
            int dirCount = entryModel.GetFolderCount();

            for (int i = 0; i < entryModel.Count; ++i)
            {
                bool isFile = (i >= dirCount);
                var entry = entryModel[i].Value;
                string path = entryModel[i].Key;
                string textStyle = GetTextStyle(path, entry.Status, mode);

                Hashtable properties = new Hashtable();
                properties.Add("@textStyle", textStyle);
                properties.Add("@folderName", entry.Name);
                properties.Add("@fileName", entry.Name);
                properties.Add("@name", entry.Name);
                properties.Add("path", path);
                properties.Add(imgTypes[ImageType.PREVIEW], this);
                properties.Add(imgTypes[ImageType.FILE_IMAGE], this);
                properties.Add(imgTypes[ImageType.FOLDER_IMAGE], this);
                properties.Add(UP_ENTRY_KEY, (entry.Type == EntryType.UP));

                //Workaround to bypass GBPVR image caching.
                properties[path] = (imgIds.ContainsKey(path)
                    ? imgIds[path] : imgIds[path] = 0);

                object uiEntry = (i < dirCount) ?
                    (object)new UiList.Container() { properties = properties } :
                    (object)new UiList.Item() { properties = properties };
                itemList.Add(uiEntry);
            }

            return itemList;
        }


        /// <summary>
        /// Find an unitialized entry in the given entryModel range and update with db info.
        /// </summary>
        protected bool ReadDbSingle(int start, int count)
        {
            bool foundNull = false;
            int end = start + count - 1;
            int dirCount = entryModel.GetFolderCount();
            for (int i = Math.Max(dirCount, start); i <= end && !foundNull; ++i)
            {
                string path = entryModel[i].Key;
                var entry = entryModel[i].Value;
                foundNull = (entry.Status == Playback.NULL);
                foundNull &= (!playbackCache.ContainsKey(path) || playbackCache[path] == Playback.NULL);
                if (foundNull) entry.Status = playbackCache[path] = GetPlaybackStatus(path);
                else entry.Status = playbackCache[path];
            }
            return foundNull;
        }


        /// <summary>
        /// Loop, finding all unitialized entries in entryModel and updating with db info.
        /// Passed to threads as an action.
        /// </summary>
        protected void ReadDbInfo()
        {
            string path;
            while (true)
            {
                lock (entryModel)
                {
                    Monitor.Wait(entryModel);
                    path = entryModel.CurrentPath;
                }

                dbConnection.Open();
                while (path.Equals(entryModel.CurrentPath))
                {
                    lock (entryModel)
                    {
                        if (path.Equals(entryModel.CurrentPath))
                        {
                            int viewCount = viewCounts[base.uiList.getViewMode()];
                            int top = entryModel.BottomIndex - viewCount;
                            if (!ReadDbSingle(top, viewCount) &&
                                !ReadDbSingle(0, entryModel.Count))
                                break;
                        }
                    }
                    Thread.Sleep(0);
                }
                try { dbConnection.Close(); }
                catch (DbException e) { Logger.Error(e.Message); }
            }
        }


        /// <summary>
        /// Loop, loading and caching images.  Passed to threads as an action.
        /// </summary>
        protected void CacheImages()
        {
            while (true)
            {
                ImageInfo info;
                lock (imgRequests)
                {
                    while (imgRequests.Count == 0)
                        Monitor.Wait(imgRequests);
                    info = imgRequests[0];
                }
                CacheImage(info.Type, info.Paths[0], info.ValX, info.ValY);
                lock (imgRequests) imgRequests.Remove(info);
            }
        }





        //CLEAN UP
        protected void CacheImage(ImageType imgType, string path, int width, int height)
        {
            Thread.Sleep(0);
            bool dbg = false;

            string imgPath = null;
            bool isDvd = (Directory.Exists(path) && Path.GetFileName(path).Equals("VIDEO_TS"));
            if (Regex.IsMatch(path, picExtensions, RegexOptions.IgnoreCase))
                imgPath = path;
            if (imgPath == null)
                imgPath = FindCoverArt(path, entryModel.FileExtensions, strictFolderMetadata);
            if (!File.Exists(imgPath))
                imgPath = (isDvd || File.Exists(path) ? fileImgPath : folderImgPath);
            bool isNullImg = (!File.Exists(imgPath));


            Thread.Sleep(0);
            if (dbg) Logger.Verbose("__isNullImg: " + isNullImg + ", " + path);
            if (dbg) Logger.Verbose("__imgNotCached: " + imgType + ", " + imgPath + ", " + width + "x" + height);


            Bitmap img = null;
            lock (imgCache)
            {
                int maxWidth = width;
                for (int i = 0; i < imgCache.Count; ++i)
                {
                    var tagType = ((ImageInfo)imgCache[i].Tag).Type;
                    var tagPaths = ((ImageInfo)imgCache[i].Tag).Paths;
                    string tagImgPath = ((ImageInfo)imgCache[i].Tag).ImgPath;
                    bool isSameImage = (tagPaths.Contains(path) || imgPath.Equals(tagImgPath));
                    if ((isNullImg && tagType == ImageType.NULL) ||
                        (isSameImage && imgCache[i].Width == width && imgCache[i].Height == height))
                    {
                        if (dbg) Logger.Verbose("__foundCached: " + path + ", " + imgPath);
                        img = imgCache[i];
                        ((ImageInfo)img.Tag).Paths.Add(path);
                        Monitor.Pulse(imgCache);
                        doNeedRendering = true;
                        return;
                    }
                    else if (isSameImage && imgCache[i].Width >= maxWidth)
                    {
                        img = imgCache[i];
                        maxWidth = imgCache[i].Width;
                    }
                }
                if (isNullImg && img == null)
                {
                    if (dbg) Logger.Verbose("__addingNullImg: " + path);
                    img = new Bitmap(1, 1);
                    img.Tag = new ImageInfo(path, null, ImageType.NULL, img.Width, img.Height);
                    imgCache.Add(img);
                    Monitor.Pulse(imgCache);
                    doNeedRendering = true;
                    return;
                }
                else if (img != null) {
                    var tmpTag = (ImageInfo)img.Tag;
                    img = (Bitmap)img.Clone();
                    img.Tag = tmpTag;
                }

                if (dbg) Logger.Verbose("__foundCached: " + path + ", " + imgPath + ", maxWidth=" + maxWidth);
            }


            Thread.Sleep(0);


            if (img == null)
            {
                FileStream fstream = new FileStream(imgPath, FileMode.Open);
                byte[] buffer = new byte[fstream.Length];
                for (int read = 0; read != buffer.Length; Thread.Sleep(0))
                    read += fstream.Read(buffer, read, Math.Min(64 * 1024, buffer.Length - read));
                fstream.Close();
                img = new Bitmap(new MemoryStream(buffer));

                //img = new Bitmap(imgPath);


                /*
                var memStream = new MemoryStream();
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Compression, IMG_COMPRESSION);
                var codecInfo = ImageCodecInfo.GetImageDecoders().First(x => x.MimeType.Equals("image/jpeg"));
                img.Save(memStream, codecInfo, encoderParams);
                img = new Bitmap(memStream);
                */

                img.Tag = new ImageInfo(path, imgPath, imgType, 0, 0);

                /*
                lock (imgCache)
                {
                    imgCache.Add(img);
                    var tmpTag = img.Tag;
                    img = (Bitmap)img.Clone();
                    img.Tag = tmpTag;
                }
                */

            }



            Thread.Sleep(0);



            var tag = (ImageInfo)img.Tag;
            if (tag.ValX > 0 || tag.ValY > 0)
            {
                int cloneWidth = img.Width - tag.ValX * 2;
                int cloneHeight = img.Height - tag.ValY * 2;
                var cloneRect = new Rectangle(tag.ValX, tag.ValY, cloneWidth, cloneHeight);
                img = img.Clone(cloneRect, img.PixelFormat);
            }
            float tmpRatio = (float)width / height;
            float imgRatio = (float)img.Width / img.Height;
            int newWidth = (int)(tmpRatio <= imgRatio ? width : height * imgRatio);
            int newHeight = (int)(tmpRatio >= imgRatio ? height : width / imgRatio);
            int xPos = (width / 2 - newWidth / 2);
            int yPos = (height / 2 - newHeight / 2);

            Thread.Sleep(0);

            var bitmap = new Bitmap(width, height);
            var g = Graphics.FromImage(bitmap);
            g.InterpolationMode = (imgType == ImageType.PREVIEW
                ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear);
            g.DrawImage(img, xPos, yPos, newWidth, newHeight);
            g.Dispose();



            Thread.Sleep(0);



            lock (imgCache)
            {
                if (dbg) Logger.Verbose("__imgCacheCount: " + imgCache.Count);


                /*
                while (imgCache.Count > IMG_CACHE_LEN - 1)
                    imgCache.RemoveAt(0);
                */


                imgCache.RemoveAll(x =>
                    imgCache.Count > IMG_CACHE_LEN &&
                    (((ImageInfo)x.Tag).Type == ImageType.FILE_IMAGE ||
                    ((ImageInfo)x.Tag).Type == ImageType.FOLDER_IMAGE));

                imgCache.RemoveAll(x =>
                    imgCache.Count > IMG_CACHE_LEN &&
                    ((ImageInfo)x.Tag).Type != ImageType.NULL);




                /*
                while (imgCache.Count > IMG_CACHE_LEN - 1)
                {
                    int imgIdx = -1;
                    int maxWidth = -1;
                    for (int i = 0; i < imgCache.Count; ++i)
                    {
                        if (imgCache[i].Width > maxWidth)
                        {
                            imgIdx = i;
                            maxWidth = imgCache[i].Width;
                        }
                    }
                    imgCache.RemoveAt(imgIdx);
                }
                */

                bitmap.Tag = new ImageInfo(path, imgPath, imgType, xPos, yPos);
                imgCache.Add(bitmap);
                Monitor.Pulse(imgCache);
            }

            if (dbg) Logger.Verbose("__finished loading: " + imgPath + ", " + width + "x" + height);

            doNeedRendering = true;
            GC.Collect();
        }





        /// <summary>
        /// Optionally wait some time for a cache update then search it for an image.
        /// </summary>
        protected Image GetCachedImage(string path, int width, int height, TimeSpan? timeout)
        {
            Image img = null;
            lock (imgCache)
            {
                if (timeout.HasValue)
                    Monitor.Wait(imgCache, timeout.Value);
                int imgIdx = imgCache.FindIndex(x =>
                    ((ImageInfo)x.Tag).Paths.Contains(path) &&
                    (((ImageInfo)x.Tag).Type == ImageType.NULL ||
                    x.Width == width || x.Height == height));
                if (imgIdx >= 0)
                {
                    img = (Image)imgCache[imgIdx].Clone();
                    img.Tag = imgCache[imgIdx].Tag;
                }
            }
            return img;
        }


        /// <summary>
        /// Fill the list with folders and files contained by cwd.
        /// </summary>
        protected int GetEntries(string cwd, bool flatten)
        {
            int dirCount = 0;
            int origCount = entryModel.Count;
            bool doBracket = bracketDirNames && !entryModel.AtTopConfigDir();
            foreach (var dir in new DirectoryInfo(cwd).GetDirectories())
            {
                if (!IsValidDir(dir.FullName, entryModel.FileExtensions, onlyDirsWithVids))
                    continue;
                else if (dir.Name.Equals("VIDEO_TS"))
                {
                    string name = Path.GetFileName(cwd) + ".dvd";
                    var entry = new Entry() { Type = EntryType.DVD, Name = name, Created = dir.CreationTime };
                    entryModel.RemoveRange(origCount, entryModel.Count - origCount);
                    entryModel.Add(dir.FullName, entry);
                    return (dirCount = 0);
                }
                else if (!flatten)
                {
                    string name = Path.GetFileName(dir.FullName);
                    if (doBracket) name = '[' + name + ']';
                    var entry = new Entry() { Type = EntryType.FOLDER, Name = name, Created = dir.CreationTime };
                    entryModel.Insert(dirCount++, dir.FullName, entry);
                }
                else dirCount += GetEntries(dir.FullName, false);
            }

            var files = new DirectoryInfo(cwd).GetFiles();
            foreach (var file in files)
            {
                if (file.Name.Equals("gbpvr.link"))
                {
                    string line;
                    var reader = File.OpenText(file.FullName);
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        var dirAndOptions = line.Split('?');
                        string dir = (Path.IsPathRooted(dirAndOptions[0])
                            ? dirAndOptions[0] : Path.Combine(cwd, dirAndOptions[0]));
                        string option = (dirAndOptions.Length > 1 ? dirAndOptions[1] : "");
                        bool isFlat = (String.Compare(option, "flat", true) == 0);

                        if (dir.Equals(cwd) && isFlat && !flatten)
                        {
                            entryModel.RemoveRange(origCount, entryModel.Count - origCount);
                            return (dirCount = GetEntries(dir, isFlat));
                        }
                        else if (!dir.Equals(cwd) && Directory.Exists(dir))
                            dirCount += GetEntries(dir, isFlat);
                    }
                    reader.Close();
                }
                else if (file.FullName.EndsWith(".lnk"))
                {
                    var lnk = new IWshRuntimeLibrary.WshShellClass().CreateShortcut(file.FullName);
                    string target = ((IWshRuntimeLibrary.IWshShortcut)lnk).TargetPath;
                    if (!IsValidDirOrFile(target, entryModel.FileExtensions, onlyDirsWithVids))
                        continue;
                    bool isDirLink = Directory.Exists(target);
                    var type = (isDirLink ? EntryType.FOLDER : EntryType.FILE);
                    string name = Path.GetFileNameWithoutExtension(file.FullName) + "*";
                    var entry = new Entry() { Type = type, Name = name, Created = file.CreationTime, IsLink = true };
                    entryModel.Insert((isDirLink ? dirCount++ : entryModel.Count), target, entry);
                }
                else if (IsValidFile(file.FullName, entryModel.FileExtensions))
                {
                    string name = (entryModel.ShowExtensions ? Path.GetFileName(file.FullName)
                        : Path.GetFileNameWithoutExtension(file.FullName));
                    foreach (var subFile in files)
                    {
                        string subBase = Path.GetFileNameWithoutExtension(file.FullName);
                        string regex = Regex.Escape(subBase) + @".*\.(smi|srt|ssa|sub)$";
                        if (Regex.IsMatch(subFile.FullName, regex, RegexOptions.IgnoreCase))
                        {
                            name += "\"";
                            break;
                        }
                    }
                    var entry = new Entry() { Type = EntryType.FILE, Name = name, Created = file.CreationTime };
                    entryModel.Add(file.FullName, entry);
                }
            }

            return dirCount;
        }


        /// <summary>
        /// Delete indexed entry from both the disk and view.
        /// </summary>
        protected void DeleteEntry(int index)
        {
            try
            {
                //Delete entry from the disk.
                var path = entryModel[index].Key;
                var entry = entryModel[index].Value;
                if (entry.Type == EntryType.FILE)
                    File.Delete(path);
                if (entry.Type == EntryType.FOLDER)
                    Directory.Delete(path, true);
                if (entry.IsLink)
                {
                    string link = entry.Name.Replace("*", ".lnk");
                    File.Delete(Path.Combine(entryModel.GetDirectory(), link));
                }

                //Delete entry from the view.
                lock (entryModel) entryModel.RemoveAt(index);
                entryModel.BottomIndex = Math.Min(entryModel.BottomIndex, entryModel.Count - 1);
                entryModel.CurrentIndex = Math.Min(index, entryModel.Count - 1);
                entryModel.CurrentPath = entryModel[entryModel.CurrentIndex].Key;
                entryModel.NeedsRefreshing = true;
            }
            catch (Exception e) { Logger.Error(e.Message); }
        }


        /// <summary>
        /// Wrap GBPVR v1.3.11 call in its own function so try/catch will work.
        /// </summary>
        protected void PlayVideoFile(string path, int position)
        {
            pluginHelper.PlayVideoFile(Path.GetFileName(path), "", path, position);
        }


        /// <summary>
        /// If not resuming, try to start video playback from the beginning.
        /// </summary>
        protected void PlayVideoFile(string path, bool resume)
        {
            if (!resume)
            {
                try { PlayVideoFile(path, 0); }
                catch (MissingMethodException e) { resume = true; }
            }
            if (resume) pluginHelper.PlayVideoFile(path);
        }


        /// <summary>
        /// Called by the framework in response to a plugin button being pressed.
        /// </summary>
        protected void HandleCommand(Command cmd)
        {
            Logger.Verbose("VideosLibrary::handleCommand; " + cmd + ", idx " + entryModel.CurrentIndex);

            int selected = entryModel.CurrentIndex;
            string path = entryModel[selected].Key;
            var entry = entryModel[selected].Value;

            ////////////////////////////////////////////////////////////////////////////////////////////entryModel locking?
            if (cmd == Command.PLAY)
            {
                entry.Status = playbackCache[path] = Playback.NULL;
                entry.NeedsRefreshing = true;
                entryModel.NeedsRefreshing = true;

                if (entry.Type == EntryType.DVD)
                    pluginHelper.PlayDVDFromDirectory(Path.GetDirectoryName(path));
                else if (entry.Type == EntryType.FILE && path.EndsWith(".iso"))
                    pluginHelper.PlayDVDFromISO(path);
                else if (entry.Type == EntryType.FILE)
                    PlayVideoFile(path, (entry.Status != Playback.UNWATCHED));
                else
                {
                    bool reload = entryModel.NeedsReloading = true;
                    if ((reload = (entry.Type == EntryType.UP))) entryModel.PopLevel();
                    else if ((reload = (Directory.Exists(path)))) entryModel.PushLevel(path);
                    if (reload)
                    {
                        WatcherInit(path);
                        PopulateListWidget();
                        lock (imgRequests) imgRequests.Clear();
                    }
                    entryModel.NeedsRefreshing = entryModel.NeedsReloading = false;
                }
            }
            else if (cmd == Command.MODE)
            {
                int endMode = (int)UiList.ViewMode.MODE_MMC_BUTTONS;
                int newMode = ((int)base.uiList.getViewMode() + 1) % endMode;
                var mode = (UiList.ViewMode)newMode;
                base.uiList.setViewMode(mode);
                if (mode <= UiList.ViewMode.MODE_DETAILS)
                {
                    lock (imgRequests) imgRequests.Clear();
                    lock (imgCache) imgCache.Clear();
                    GC.Collect();
                }
                SetRegData(REG_VIEW_NAME, mode.ToString().Replace("MODE_", ""));
                entryModel.NeedsRefreshing = true;
                viewModeModified = true;
            }
            else if (cmd == Command.SORT)
            {
                int endSort = (int)SortMethod.SHUFFLE + 1;
                int sorting = ((int)entryModel.Sorting + 1) % endSort;
                entryModel.Sort((entryModel.Sorting = (SortMethod)sorting));
                entryModel.LastManualSort = DateTime.Now;
                entryModel.NeedsRefreshing = true;
                
                if (entryModel.Sorting != SortMethod.SHUFFLE)
                    SetRegData(REG_SORT_NAME, entryModel.Sorting);
            }
            else if (cmd == Command.PLAY_ALL)
            {
                var queue = new Queue();
                int start = base.uiList.getContainerCount();
                for (int i = start; i < entryModel.Count; ++i)
                    queue.Enqueue(entryModel[i].Key);
                pluginHelper.PlayVideoFiles(queue);
            }
            else if (cmd == Command.DELETE)
            {
                string type =
                    (entry.IsLink) ? "link and target" :
                    (entry.Type == EntryType.FOLDER) ? "folder" :
                    (entry.Type == EntryType.FILE) ? "file" : null;
                if (selected > 0 && !entryModel.AtTopConfigDir() && type != null)
                {
                    if (confirmDelete)
                    {
                        string name = entry.Name;
                        if (entry.IsLink) name += "\n" + path;
                        string msg = "Are you sure you want to delete this " + type + "?\n\n" + name;
                        setPopup(new DeletePopup(this, msg, selected));
                    }
                    else DeleteEntry(selected);
                }
            }
            else if (cmd == Command.MAIN_MENU)
            {
                pluginHelper.ReturnToMainMenu();
            }
        }


        #endregion "Helpers"


        #region "Overrides"
        //#########################################################################################
        //#########################################################################################


        /// <summary>
        /// This method will be called by the popup to let the parent know of some outcome 
        /// </summary>
        /// <param name="popup">popup</param>
        /// <param name="command">name of button clicked</param>
        public virtual void handlePopupCallback(object popup, string command)
        {
            if (popup is DeletePopup && command.Equals("OK"))
                DeleteEntry(((DeletePopup)popup).Index);
            base.setPopup(null);
            doNeedRendering = true;
        }


        /// <summary>
        /// Called by the parent when images are needed for on-screen entries.
        /// </summary>
        public virtual Image GetImage(Hashtable parameters, string name, int width, int height)
        {
            Logger.Verbose("VideosLibrary::GetImage; " +
                imgTypes.First(x => x.Value.Equals(name)).Key + ", " +
                parameters["path"].ToString() + ", " + width + "x" + height);
            
            ImageType imgType = imgTypes.First(x => x.Value.Equals(name)).Key;
            bool isUpImg = Boolean.Parse(parameters[UP_ENTRY_KEY].ToString());
            string path = parameters["path"].ToString();
            string searchPath = (isUpImg ? upImgPath : path);
            Image img = GetCachedImage(searchPath, width, height, null);
            if (img == null)
            {
                lock (imgRequests)
                {
                    var info = new ImageInfo(searchPath, null, imgType, width, height);
                    if (imgRequests.Count == 0 || !imgRequests[0].Equals(info))
                    {
                        imgRequests.RemoveAll(x => x.Equals(info));
                        imgRequests.Insert(0, info);
                        Monitor.Pulse(imgRequests);
                    }
                }
            }

            var start = DateTime.Now;
            int ms = (imgType == ImageType.PREVIEW ? 25 : 0);
            var timeout = new TimeSpan(0, 0, 0, 0, ms);
            while (img == null && timeout.TotalMilliseconds >= 0)
            {
                img = GetCachedImage(searchPath, width, height, timeout);
                timeout = timeout.Subtract(DateTime.Now.Subtract(start));
            }
            if (img == null)
            {
                //Workaround to bypass GBPVR image caching.
                parameters[RERENDER_KEY] = "true";
                parameters[path] = imgIds[path]++;
                return null;
            }
            else return (((ImageInfo)img.Tag).Type == ImageType.NULL) ? null : img;
        }


        /// <summary>
        /// Called by framework to get the list of buttons to show
        /// </summary>
        /// <returns>buttons to be shown</returns>
        protected override string[] getButtonList()
        {
            var list = new List<string>();
            commands.ForEach(pair => list.Add(pair.Value));
            return list.ToArray();
        }


        /// <summary>
        /// Provides the config application with a settings form.
        /// </summary>
        public override Form GetConfigFormInstance(XmlDocument config)
        {
            return new VideosLibraryForm(this, config);
        }


        /// <summary>
        /// Called by the framework to a description of
        /// the plugin (for displaying on the main menu)
        /// </summary>
        /// <returns>plugin description</returns>
        public override string getDescription()
        {
            return "Watch your videos";
        }


        /// <summary>
        /// Called by the framework to get the name of the plugin. This is
        /// the name that will ultimately be shown on the main menu button.
        /// </summary>
        /// <returns>plugin name</returns>
        public override string getName()
        {
            return "Videos Library";
        }


        /// <summary>
        /// Gets the subdirectory name in the skin which
        /// holds the resources for this plugin (skin.xml etc)
        /// </summary>
        /// <returns>plugin's skin subdirectory</returns>
        public override string getSkinSubdirectory()
        {
            return "Videos Library";
        }


        /// <summary>
        /// Append additional render elements to the default set.
        /// </summary>
        public override ArrayList GetRenderList()
        {
            var list = base.GetRenderList();
            if (!activated) return list;

            var parameters = new Hashtable();
            bool isFolder = (entryModel.CurrentIndex < entryModel.GetFolderCount());

            parameters["name"] = "StatusInfo";
            double elapsed = DateTime.Now.Subtract(entryModel.LastManualSort).TotalSeconds;
            string sortingStr = "Sort: " + (
                (entryModel.Sorting == SortMethod.ALPHA_NUMERIC) ? "AlphaNumeric" :
                (entryModel.Sorting == SortMethod.CREATION) ? "CreationTime" :
                (entryModel.Sorting == SortMethod.SHUFFLE) ? "Shuffle" : "?");
            parameters["alpha"] = Math.Max(0, 255 - (decimal)Math.Pow(elapsed, 3));
            parameters["@" + parameters["name"]] = sortingStr;
            AddUiElement(list, parameters, ref statusUi);

            parameters["name"] = (isFolder ? "FolderSummary" : "InfoSummary");
            //TEMP: REDUNDANT SKIN CHECKS TO AVOID UNNEEDED XML SEARCH.
            if (skinHelper2.checkPlacementRectDefined(parameters["name"].ToString()) &&
                skinHelper2.checkCompositeImageDefined(parameters["name"].ToString()) &&
                GetXmlMetadata(parameters, FindXmlFile(entryModel.CurrentPath,
                                                       entryModel.FileExtensions,
                                                       strictFolderMetadata)))
            {
                AddUiElement(list, parameters, ref summaryUi);
            }

            parameters["name"] = (isFolder ? "FolderArt" : "CoverArt");
            parameters["path"] = entryModel.CurrentPath;
            parameters[imgTypes[ImageType.PREVIEW]] = this;
            parameters[UP_ENTRY_KEY] = (entryModel.CurrentEntry.Type == EntryType.UP);
            AddUiElement(list, parameters, ref coverUi);

            return list;
        }


        /// <summary>
        /// Update model, return whether the view should be refreshed.
        /// </summary>
        public override bool needsRendering()
        {
            bool doRender = base.needsRendering();
            if (!activated || base.state == State.Inactive) return doRender;

            for (int i = 0; i < entryModel.Count; ++i)
            {
                var entry = entryModel[i].Value;
                bool isFile = (i >= entryModel.GetFolderCount());
                if (isFile && entry.NeedsRefreshing && entry.Status != Playback.NULL)
                {
                    int viewCount = viewCounts[base.uiList.getViewMode()];
                    int btm = entryModel.BottomIndex;
                    int top = btm - (viewCount - 1);
                    entryModel.NeedsRefreshing |= (top <= i && i <= btm);
                    entry.NeedsRefreshing = !entryModel.NeedsRefreshing;
                }
                else if (isFile && entry.NeedsRefreshing && entry.Status == Playback.NULL)
                    lock (entryModel) Monitor.Pulse(entryModel);
            }
            double elapsed = DateTime.Now.Subtract(entryModel.LastReload).TotalSeconds;
            bool isShuffled = (entryModel.Sorting == SortMethod.SHUFFLE);
            entryModel.NeedsReloading |= (elapsed > RELOAD_TIMEOUT && !isShuffled);

            bool doPopulate = (entryModel.NeedsRefreshing || entryModel.NeedsReloading);
            doRender = doRender || doNeedRendering || doPopulate;
            doNeedRendering = entryModel.NeedsRefreshing = false;
            if (doPopulate) PopulateListWidget();

            return doRender;
        }


        /// <summary>
        /// Called internally and by the parent framework to refresh the UiList.
        /// </summary>
        protected override void PopulateListWidget()
        {
            ignoreNextSelect = activated;
            base.PopulateListWidget();
            if (!activated) return;

            //Get the configured dirs, their subdirs, and video files.
            if (entryModel.NeedsReloading)
            {
                lock (entryModel)
                {
                    entryModel.Clear();
                    entryModel.NeedsReloading = false;
                    entryModel.LastReload = DateTime.Now;
                    string cwd = entryModel.GetDirectory();
                    if (entryModel.AtTopConfigDir() || !Directory.Exists(cwd))
                    {
                        foreach (var topDir in entryModel.TopDirs)
                        {
                            var entry = new Entry() { Type = EntryType.FOLDER, Name = topDir.Value };
                            entryModel.Add(topDir.Key, entry);
                        }
                    }
                    else
                    {
                        GetEntries(cwd, false);
                        if (!entryModel.AtTopDir())
                        {
                            var entry = new Entry() { Type = EntryType.UP, Name = "[..]" };
                            string upPath = entryModel.FolderStack.Peek();
                            entryModel.Insert(0, upPath, entry);
                            entryModel.Sort();
                        }
                        entryModel.BottomIndex = Math.Min(entryModel.BottomIndex, entryModel.Count - 1);
                        entryModel.CurrentIndex = Math.Min(entryModel.CurrentIndex, entryModel.Count - 1);
                        if (entryModel.Contains(entryModel.CurrentPath))
                            entryModel.CurrentIndex = entryModel.IndexOf(entryModel.CurrentPath);
                        else entryModel.CurrentPath = entryModel[entryModel.CurrentIndex].Key;
                    }
                    Monitor.Pulse(entryModel);
                }
            }
            else
            {
                entryModel.CurrentPath = entryModel[entryModel.CurrentIndex].Key;
                entryModel.CurrentEntry = entryModel[entryModel.CurrentIndex].Value;
            }

            //Finish with assignments to the base class.
            base.uiList.setViewMode(UpdatedViewMode());
            base.uiList.setItemList(UpdatedUiList());
            SelectedItem(base.uiList.getItemList()[entryModel.CurrentIndex]);

            Logger.Verbose("VideosLibrary::PopulateListWidget; " +
                "bottom, currentIdx, currentPath, selected, count: " +
                entryModel.BottomIndex + ", " + entryModel.CurrentIndex + ", " +
                entryModel.CurrentPath + ", " + base.uiList.getSelectedItemIndex() + ", " +
                entryModel.Count);
        }


        /// <summary>
        /// Called by the framework in response to a plugin button being pressed
        /// </summary>
        /// <param name="command">name of button clicked</param>
        protected override void handleCommand(string command)
        {
            int cmdIdx = commands.FindIndex(pair => pair.Value.Equals(command));
            if (cmdIdx >= 0) HandleCommand(commands[cmdIdx].Key);
            else base.handleCommand(command);
        }


        /// <summary>
        /// Called when the user presses a key on the remote or keyboard
        /// </summary>
        /// 
        public override bool OnKeyDown(KeyEventArgs e)
        {
            string mapping = keyHelper.KeyMapping(e) ?? "";
            bool atTopDir = entryModel.AtTopDir();
            bool isPopup = (base.activePopup != null);

            if (false && e.Modifiers == 0 && 'A' <= e.KeyValue && e.KeyValue <= 'Z')
            {
                string letter = ((char)e.KeyValue).ToString();
                int idx = entryModel.FindIndex(x => x.Value.Name.StartsWith(letter));
                if (idx != -1) SelectedItem(base.uiList.getItemList()[idx]);
            }
            else if (mapping.Equals("LIBRARY_FASTFORWARD") || mapping.Equals("LIBRARY_SKIP_FORWARDS"))
            {
                SelectedItem(base.uiList.getItemList()[entryModel.Count - 1]);
            }
            else if (mapping.Equals("LIBRARY_REWIND") || mapping.Equals("LIBRARY_SKIP_BACKWARDS"))
            {
                SelectedItem(base.uiList.getItemList()[0]);
            }
            else if (mapping.Equals("LIBRARY_ESCAPE") && !atTopDir && !isPopup)
            {
                entryModel.CurrentIndex = 0;
                HandleCommand(Command.PLAY);
            }
            else if (mapping.Equals("LIBRARY_TOGGLE_VIEW"))
                HandleCommand(Command.MODE);
            else if (mapping.Equals("LIBRARY_SHUFFLE"))
                HandleCommand(Command.SORT);
            else if (e.KeyCode == Keys.Delete)
                HandleCommand(Command.DELETE);
            else return base.OnKeyDown(e);

            return (e.Handled = true);
        }

        
        /// <summary>
        /// Called when the user changes the list entry selection.
        /// </summary>
        /// <param name="o">selected object from list</param>
        public override void SelectedItem(object o)
        {
            Logger.Verbose("VideosLibrary::SelectedItem; " +
                base.uiList.getSelectedItemIndex() + ", " + !ignoreNextSelect);

            if (ignoreNextSelect)
            {
                ignoreNextSelect = false;
                return;
            }

            int count = entryModel.Count;
            int btm = entryModel.BottomIndex;
            Hashtable properties = (o is UiList.Item
                ? ((UiList.Item)o).properties : ((UiList.Container)o).properties);
            int idx = entryModel.FindIndex(x => x.Value.Name.Equals(properties["@name"]));
            int viewCount = viewCounts[base.uiList.getViewMode()];


            //TEMP LOGIC
            int iconCols = 4;
            if (base.uiList.getViewMode() == UiList.ViewMode.MODE_ICON && (idx >= btm))
            {
                btm = idx + (iconCols - (idx % iconCols) - 1);
                btm = Math.Min(btm, count - 1);
            }
            else if (base.uiList.getViewMode() == UiList.ViewMode.MODE_ICON)
            {
                btm = Math.Min(btm, idx + (viewCount - (idx % iconCols) - 1));
            }
            else
            {
                btm = (idx >= btm) ? idx : Math.Min(btm, idx + (viewCount - 1));
                btm = Math.Max(btm, Math.Min(viewCount, entryModel.Count) - 1);
            }


            entryModel.BottomIndex = btm;
            entryModel.CurrentIndex = idx;
            entryModel.CurrentPath = entryModel[idx].Key;
            entryModel.CurrentEntry = entryModel[idx].Value;
            base.uiList.suggestSelectionIndex(entryModel.BottomIndex);
            base.uiList.suggestSelectionIndex(entryModel.CurrentIndex);
            base.SelectedItem(o);
        }


        /// <summary>
        /// Called when the user activates a list entry (double click/'enter'/etc).
        /// </summary>
        /// <param name="o">selected object from list</param>
        public override void ActivateItem(object o)
        {
            HandleCommand(Command.PLAY);
            base.ActivateItem(o);
        }


        /// <summary>
        /// Called when the plugin is first accessed.
        /// </summary>
        public override void Activate()
        {
            Logger.Verbose("VideosLibrary::Activate; " + activated);

            //Only activate this class once.
            if (activated)
            {
                base.Activate();
                return;
            }

            //Early initialization of base variable.
            string skinRelPath = Path.Combine(getSkinSubdirectory(), "skin.xml");
            skinHelper2 = new SkinHelper2(skinRelPath);

            //Initialize class variables.
            imgCache = new List<Bitmap>();
            imgRequests = new List<ImageInfo>();
            imgIds = new Dictionary<string, int>();
            imgTypes = new Dictionary<ImageType, string>()
            {
                { ImageType.PREVIEW, "@previewImage" },
                { ImageType.FILE_IMAGE, "@thumbnail" },
                { ImageType.FOLDER_IMAGE, "@folderPreviewImage" }
            };
            entryModel = new EntryModel();
            keyHelper = new KeyCommandHelper("video.xml");
            commands = new List<KeyValuePair<Command, string>>();
            viewCounts = GetViewableCounts(skinHelper2);
            pluginHelper = PluginHelperFactory.getPluginHelper();
            dbConnection = DatabaseHelperFactory.getDbProviderFactory().CreateConnection();
            dbConnection.ConnectionString = DatabaseHelperFactory.getDbConnectionString();

            //Get paths for various skin images.
            string skinDir = pluginHelper.GetSkinRootDirectory().Replace(@"\skin", @"\skin2");
            string imgPath = Path.Combine(skinDir, "_CoreImages");
            upImgPath = Path.Combine(imgPath, "Folder-Up.png");
            fileImgPath = Path.Combine(imgPath, "Video-File.png");
            folderImgPath = Path.Combine(imgPath, "Video-Folder.png");

            //Assign configuration values to both member variables and options map.
            bool slick = Regex.IsMatch(skinDir, @"skin2\\slick", RegexOptions.IgnoreCase);
            options = InitOptions(pluginHelper.GetConfiguration());
            options["ShowPlay"] = showMode = (showMode && !slick);

            //Load translations for button text.
            if (showPlay) commands.Add(MakePair(Command.PLAY, skinHelper2.getTranslation("Play")));
            if (showMode) commands.Add(MakePair(Command.MODE, skinHelper2.getTranslation("Mode")));
            if (showSort) commands.Add(MakePair(Command.SORT, skinHelper2.getTranslation("Sort")));
            if (showPlayAll) commands.Add(MakePair(Command.PLAY_ALL, skinHelper2.getTranslation("Play All")));
            if (showDelete) commands.Add(MakePair(Command.DELETE, skinHelper2.getTranslation("Delete")));
            if (showMainMenu) commands.Add(MakePair(Command.MAIN_MENU, skinHelper2.getTranslation("Main Menu")));

            //Query registry for the current sort method.
            string sortStr = GetRegData(REG_SORT_NAME, "ALPHA_NUMERIC");
            entryModel.Sorting = (SortMethod)Enum.Parse(typeof(SortMethod), sortStr);

            //Start a thread to retrieve database info.
            var dbThread = new Thread(ReadDbInfo);
            dbThread.Priority = ThreadPriority.Lowest;
            dbThread.IsBackground = true;
            dbThread.Start();

            //Start a thread to read and cache requested images.
            var imgThread = new Thread(CacheImages);
            imgThread.Priority = ThreadPriority.Lowest;
            imgThread.IsBackground = true;
            imgThread.Start();

            //Activate base class.
            activated = true;
            base.Activate();
        }


        #endregion "Overrides"


    } //class VideosLibrary

} //namespace VideosLibraryPlugin