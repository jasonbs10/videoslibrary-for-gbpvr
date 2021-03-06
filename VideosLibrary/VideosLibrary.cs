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
        protected const int IMG_CACHE_LEN = 15;
        protected const float RELOAD_TIMEOUT = 180;
        protected const float WATCHED_PADDING = 10;
        protected const string IS_VALID_KEY = "isValid";
        protected const string IS_CACHED_KEY = "isCached";
        protected const string IMG_PATH_KEY = "imagePath";
        protected const string ENTRY_TYPE_KEY = "entryType";
        protected const string RERENDER_KEY = "FORCE_RERENDER";
        protected const string REG_VIEW_NAME = "VideoLibraryView";
        protected const string REG_SORT_NAME = "VideoLibrarySort";
        protected const string REG_KEY = @"HKEY_CURRENT_USER\Software\devnz";
        protected const string XML_XPATH = @"/settings/PluginSettings/VideosLibrary/";

        protected bool confirmDelete;// = true;
        protected bool hybridViewMode;// = false;
        protected bool bracketDirNames;// = true;
        protected static bool onlyDirsWithVids;// = false; //TEMP STATIC
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
        protected GBPVRUiElement backgroundUi = null;
        
        protected List<ImageInfo> imgRequests = null;
        protected List<PackedImage> imgCache = null;
        protected Dictionary<string, int> imgIds = null;
        protected Dictionary<ImageType, string> imgTypes = null;

        protected EntryModel entryModel = null;
        protected IPluginHelper pluginHelper = null;
        protected KeyCommandHelper keyHelper = null;
        protected FileSystemWatcher fileWatcher = null;
        protected static DbConnection dbConnection = null; //TEMP STATIC
        protected Dictionary<string, bool> options = null;
        protected Dictionary<ViewMode, int> viewCounts = null;
        protected List<KeyValuePair<Command, string>> commands = null;

        protected enum EntryType { NULL, UP, DVD, FOLDER, FILE }
        protected enum SortMethod { ALPHA_NUMERIC, CREATION, SHUFFLE }
        protected enum Playback { NULL, UNWATCHED, WATCHING, FINISHED }
        protected enum Command { PLAY, MODE, SORT, PLAY_ALL, DELETE, MAIN_MENU }
        protected enum ImageType { NULL, BACKGROUND, PREVIEW, FILE_IMAGE, FOLDER_IMAGE }


        

        //TEMP IMPL
        protected List<FileSystemWatcher> fileWatchers = new List<FileSystemWatcher>();
        protected Dictionary<string, Playback> playbackCache = new Dictionary<string, Playback>();
        static protected string picExtensions = @"^.+\.(bmp|jpg|png|tiff|tbn)$";
        static Dictionary<string, List<FileSystemInfo>> fileSystemCache =
            new Dictionary<string, List<FileSystemInfo>>();
        static Dictionary<string, Hashtable> xmlCache = new Dictionary<string, Hashtable>();
        protected volatile bool getXmlMetadata = false;





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
        /// Thread-safe compressed image wrapper that caches scaled image requests.
        /// </summary>
        protected class PackedImage
        {
            protected MemoryStream Mem { get; set; }
            protected List<Image> Imgs { get; set; }
            public ImageInfo Tag { get; protected set; }

            public PackedImage(Image img, ImageInfo tag)
            {
                Tag = tag;
                Imgs = new List<Image>();
                if (img != null)
                {
                    Mem = new MemoryStream();
                    Imgs = new List<Image>();
                    if (img.PixelFormat == PixelFormat.Format32bppArgb)
                        img.Save(Mem, ImageFormat.Png);
                    else img.Save(Mem, ImageFormat.Jpeg);
                }
            }

            public bool IsPacked(int width, int height)
            {
                lock (this)
                    return (Mem != null && Imgs.Count(x => x.Width == width && x.Height == height) == 0);
            }

            public void PackImage()
            {
                lock (this)
                {
                    for (int i = Imgs.Count - 1; i >= 0; --i)
                    {
                        Imgs[i].Dispose();
                        Imgs.RemoveAt(i);
                    }
                }
            }

            public void UnpackImage(int width, int height, ImageType type)
            {
                lock (this)
                {
                    if (!IsPacked(width, height))
                        return;
                    using (var inset = GetInset())
                    {
                        //Determine how to scale and position the new image.
                        float canvasRatio = (float)width / height;
                        float insetRatio = (float)inset.Width / inset.Height;
                        int newWidth = (int)(canvasRatio <= insetRatio ? width : height * insetRatio);
                        int newHeight = (int)(canvasRatio >= insetRatio ? height : width / insetRatio);
                        int newX = ((width - newWidth) / 2);
                        int newY = ((height - newHeight) / 2);

                        //On a canvas of the requested size, draw and cache the new image.
                        var img = new Bitmap(width, height);
                        using (var g = Graphics.FromImage(img))
                        {
                            g.InterpolationMode = (type <= ImageType.PREVIEW
                                 ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear);
                            g.DrawImage(inset, newX, newY, newWidth, newHeight);
                        }
                        Imgs.Add(img);
                    }
                }
            }

            public Image GetImage(int width, int height)
            {
                lock (this)
                {
                    Image img = null;
                    if (Mem != null)
                    {
                        UnpackImage(width, height, Tag.Type);
                        img = Imgs.First(x => x.Width == width && x.Height == height);
                        img = (Image)img.Clone();
                        img.Tag = Tag;
                    }
                    return img;
                }
            }

            public Image GetInset()
            {
                lock (this)
                {
                    Image img = null;
                    if (Mem != null)
                    {
                        img = new Bitmap(Mem);
                        img.Tag = Tag;
                    }
                    return img;
                }
            }
        }


        /// <summary>
        /// Represents an image; the path list contains all paths associated with the image.
        /// </summary>
        protected class ImageInfo
        {
            public List<string> Paths;
            public readonly string ImgPath;
            public readonly ImageType Type;
            public readonly int Width;
            public readonly int Height;

            public static ImageInfo NULL = new ImageInfo();
            private ImageInfo()
            {
                Paths = new List<string>();
                ImgPath = null;
                Type = ImageType.NULL;
                Width = 0;
                Height = 0;
            }

            public ImageInfo(string path, string imgPath, ImageType type, int width, int height)
            {
                Paths = new string[] { path }.ToList();
                ImgPath = imgPath;
                Type = type;
                Width = width;
                Height = height;
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
                Properties = new Hashtable();
                NeedsRefreshing = false;
            }
            public string Name { get; set; }
            public bool IsLink { get; set; }
            public EntryType Type { get; set; }
            public Playback Status { get; set; }
            public DateTime Created { get; set; }
            public Hashtable Properties { get; set; }
            public bool NeedsRefreshing { get; set; }
        }


        /// <summary>
        /// Backs the current file system view.
        /// </summary>
        protected class EntryModel : List<KeyValuePair<string, Entry>>
        {
            protected string CurrentDir = "";

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
                CurrentPath = null;
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
                return CurrentDir;
            }

            public void SetDirectory(string cwd)
            {
                CurrentDir = cwd;
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
                CurrentPath = null;
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
        /// Retrieves the columns for each row matching the given criteria.
        /// </summary>
        protected static Dictionary<string, Dictionary<string, object>>
            GetDbValues(string table, string row, string like)
        {
            var values = new Dictionary<string, Dictionary<string, object>>();
            if (dbConnection.State != ConnectionState.Open)
                return values;
            using (var cmd = dbConnection.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM " + table + " WHERE " + row + " LIKE " + like;
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string rowMatch = reader[row].ToString();
                        if (!values.ContainsKey(rowMatch))
                            values[rowMatch] = new Dictionary<string,object>();
                        for (int i = 0; i < reader.FieldCount; ++i)
                            values[rowMatch].Add(reader.GetName(i), reader.GetValue(i));
                    }
                }
            }
            return values;
        }


        /// <summary>
        /// Do a database lookup for the playback status associated with this path.
        /// </summary>
        protected static Playback GetPlaybackStatus(string path)
        {
            string dir = Path.GetDirectoryName(path);
            string relPath = Path.GetFileName(path);
            string table = "PLAYBACK_POSITION";
            string row = "filename";
            string like = "\"%" + relPath + "\"";
            var vals = GetDbValues(table, row, like);
            while (dir != null && vals.Count(x => x.Key.Contains(relPath)) > 1)
            {
                string parent = Path.GetFileName(dir);
                if (String.IsNullOrEmpty(parent)) parent = dir;
                relPath = Path.Combine(parent, relPath);
                dir = Path.GetDirectoryName(dir);
            }

            int pos = 0;
            int end = -1;
            if (vals.Count(x => x.Key.Contains(relPath)) > 0)
            {
                var cols = vals.First(x => x.Key.Contains(relPath)).Value;
                var dbPos = cols["last_position"];
                var dbEnd = cols["duration"];
                if (!(dbPos is DBNull)) pos = Convert.ToInt32(dbPos);
                if (!(dbEnd is DBNull)) end = Convert.ToInt32(dbEnd);
            }
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
        /// Only called by GetPathInfos() and GetPathInfo().
        /// Make sure directory contents are cached then return them.
        /// If the directory can't be read then null is returned.
        /// </summary>
        protected static List<FileSystemInfo> GetPathInfosImpl(string dir)
        {
            try
            {
                bool isValidDir;
                lock (fileSystemCache)
                    isValidDir = fileSystemCache.ContainsKey(dir);
                if (!isValidDir)
                {
                    var infos = new DirectoryInfo(dir).GetFileSystemInfos().ToList();
                    lock (fileSystemCache) fileSystemCache[dir] = infos;
                }
            }
            catch (Exception e)
            {
                Logger.Verbose("VideosLibrary::GetPathInfosImpl; " + dir + ", " + e);
                lock (fileSystemCache) fileSystemCache[dir] = null;
            }
            return fileSystemCache[dir];
        }


        /// <summary>
        /// Retrieve cached contents of a [file's] directory.
        /// If the directory can't be read then null is returned.
        /// </summary>
        protected static List<FileSystemInfo> GetPathInfos(string path)
        {
            var info = GetPathInfo(path);
            if (info is FileInfo)
                info = GetPathInfo(Path.GetDirectoryName(info.FullName));

            var infos = GetPathInfosImpl(info.FullName);
            if (infos != null)
                lock (fileSystemCache) infos = infos.ToList();
            return infos;
        }


        /// <summary>
        /// Retrieve cached file or directory info.
        /// </summary>
        protected static FileSystemInfo GetPathInfo(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (String.IsNullOrEmpty(dir))
                return new DirectoryInfo(path);

            bool isValidPath;
            ICollection<FileSystemInfo> infos = GetPathInfosImpl(dir);
            lock (fileSystemCache) isValidPath = (CheckPath(path, infos) != null);
            if (infos != null && !isValidPath)
            {
                var info = new FileInfo(path);
                lock (fileSystemCache) infos.Add(info);
            }
            if (infos != null)
                lock (fileSystemCache)
                    return infos.First(x => x.FullName == path);
            return new FileInfo(path);
        }


        /// <summary>
        /// Return the path if it exists, otherwise return null;
        /// </summary>
        protected static string CheckPath<T>(string path, IEnumerable<T> infos) where T : FileSystemInfo
        {
            return (infos != null && (infos.Count(x =>
                String.Compare(x.FullName, path, true) == 0 && x.Exists) != 0)
                ? path : null);
        }


        /// <summary>
        /// Return a path which matches the regex and, ignoring extension, the file name.
        /// If no such file is found, returns null.
        /// </summary>
        protected static string GetCongruentFile(string file, string regex, IEnumerable<FileInfo> files)
        {
            var imgFile = files.FirstOrDefault(x =>
                Regex.IsMatch(x.Name, regex) &&
                String.Compare
                (Path.GetFileNameWithoutExtension(x.Name),
                 Path.GetFileNameWithoutExtension(file), true) == 0);
            return (imgFile != null ? imgFile.FullName : null);
        }


        /// <summary>
        /// Make sure the path is an existent, valid directory.
        /// Pass a fileRegex to check if the directory has valid files.
        /// The checkRead flag will make sure that directory contents can be seen.
        /// </summary>
        protected static bool IsValidDir(string path, string fileRegex, bool checkRead)
        {
            var info = GetPathInfo(path);
            if (info is DirectoryInfo)
                return IsValidDir(new DirectoryInfo(path), fileRegex, checkRead);
            return false;
        }
        protected static bool IsValidDir(DirectoryInfo dir, string fileRegex, bool checkRead)
        {
            bool isRoot = (dir.FullName == dir.Root.FullName);
            bool isHidden = ((dir.Attributes & FileAttributes.Hidden) != 0);
            bool isSystem = ((dir.Attributes & FileAttributes.System) != 0);
            if (!dir.Exists || (!isRoot && (isHidden || isSystem))) return false;
            if (!checkRead) return true;

            List<FileSystemInfo> infos = GetPathInfos(dir.FullName);
            if (infos == null) return false;
            if (!onlyDirsWithVids) return true;

            foreach (var file in infos.OfType<FileInfo>())
                if (IsValidFile(file, fileRegex)) return true;
            foreach (var subdir in infos.OfType<DirectoryInfo>())
                if (IsValidDir(subdir, fileRegex, checkRead)) return true;
            return false;
        }


        /// <summary>
        /// Make sure the path is an existent, valid file which matches the regex.
        /// </summary>
        protected static bool IsValidFile(string path, string fileRegex)
        {
            var info = GetPathInfo(path);
            return (info is FileInfo ? IsValidFile(new FileInfo(path), fileRegex) : false);
        }
        protected static bool IsValidFile(FileInfo file, string fileRegex)
        {
            bool isMatch = Regex.IsMatch(file.FullName, fileRegex, RegexOptions.IgnoreCase);
            bool isHidden = ((file.Attributes & FileAttributes.Hidden) != 0);
            return (file.Exists && isMatch && !isHidden);
        }


        /// <summary>
        /// Wrapper for the individual dir and file functions.
        /// </summary>
        protected static bool IsValidPath(string path, string fileRegex, bool checkRead)
        {
            return IsValidPath(GetPathInfo(path), fileRegex, checkRead);
        }
        protected static bool IsValidPath(FileSystemInfo info, string fileRegex, bool checkRead)
        {
            return (info is FileInfo
                ? IsValidFile((FileInfo)info, fileRegex)
                : IsValidDir((DirectoryInfo)info, fileRegex, checkRead));
        }


        /// <summary>
        /// Set the path to null if invalid or update it if it represents a dvd.
        /// Returns true if the path represents a file or dvd.
        /// </summary>
        protected static bool SetCanonicalPath(ref string path, string fileRegex)
        {
            var info = GetPathInfo(path);
            bool isDir = (info is DirectoryInfo);
            bool isValid = IsValidPath(info, fileRegex, true);
            string dvdPath = Path.Combine(path, "VIDEO_TS");
            if (isValid && isDir && Path.GetFileName(path).Equals("VIDEO_TS"))
            {
                path = Path.GetDirectoryName(path);
                path = Path.Combine(path, Path.GetFileName(path)) + ".dvd_";
                return true;
            }
            else if (isValid && isDir && GetPathInfo(dvdPath).Exists)
            {
                string origDvdPath = dvdPath;
                bool isFile = SetCanonicalPath(ref dvdPath, fileRegex);
                if (origDvdPath != dvdPath) path = dvdPath;
                return isFile;
            }
            else if (!isValid)
                path = null;

            return !isDir;
        }


        /// <summary>
        /// Find an xml file for the given video file or directory.
        /// If strict, only allow folder.xml for directories.
        /// If no such file is found, returns null.
        /// </summary>
        protected static string FindXmlFile(string path, string fileRegex, bool strict)
        {
            bool isFile = SetCanonicalPath(ref path, fileRegex);
            if (path == null) return null;

            string xmlPath = null;
            string dir = (isFile ? Path.GetDirectoryName(path) : path);
            var files = (GetPathInfos(path) ?? new List<FileSystemInfo>()).OfType<FileInfo>();
            if (isFile)
                xmlPath = Path.ChangeExtension(path, ".xml");
            else
            {
                xmlPath = Path.Combine(path, "Folder.xml");
                if (CheckPath(xmlPath, files) == null && !strict)
                {
                    foreach (var file in files)
                    {
                        if (IsValidFile(file.FullName, fileRegex))
                        {
                            xmlPath = Path.ChangeExtension(file.FullName, "xml");
                            xmlPath = CheckPath(xmlPath, files);
                            if (xmlPath != null) break;
                        }
                    }
                }
            }

            if (CheckPath(xmlPath, files) == null && (isFile || !strict))
                xmlPath = Path.Combine(dir, "MyMovies.xml");
            return CheckPath(xmlPath, files);
        }


        /// <summary>
        /// Find cover art for a given video file or directory.
        /// If strict, only allow folder.jpg (or a metadata-specific image) for directories.
        /// If no such file is found, returns null.
        /// </summary>
        protected static string FindCoverArt(string path, string fileRegex, bool strict)
        {
            bool isFile = SetCanonicalPath(ref path, fileRegex);
            if (path == null) return null;

            string imgPath = null;
            var metadata = new Hashtable();
            string dir = (isFile ? Path.GetDirectoryName(path) : path);
            var files = (GetPathInfos(path) ?? new List<FileSystemInfo>()).OfType<FileInfo>();
            if (GetXmlMetadata(metadata, FindXmlFile(path, fileRegex, strict)))
            {
                imgPath = metadata[IMG_PATH_KEY].ToString();
                imgPath = CheckPath(imgPath, files) ?? Path.Combine(dir, imgPath);
                if (CheckPath(imgPath, files) != null)
                    return imgPath;
            }

            if (isFile)
                imgPath = GetCongruentFile(path, picExtensions, files);
            else
            {
                imgPath = GetCongruentFile("Folder", picExtensions, files);
                if (imgPath == null && !strict)
                {
                    foreach (var file in files)
                    {
                        if (IsValidFile(file.FullName, fileRegex))
                            imgPath = GetCongruentFile(file.Name, picExtensions, files);
                        if (imgPath != null) break;
                    }
                }
            }
            return imgPath;
        }


        /// <summary>
        /// Fill table with tag|metadata pairs from the provided xml file and return status.
        /// </summary>
        protected static bool GetXmlMetadata(Hashtable properties, string xmlPath)
        {
            Hashtable metadata = null;
            if (xmlPath == null)
             return (bool)(properties[IS_VALID_KEY] = false);
            else if (xmlCache.ContainsKey(xmlPath))
                metadata = xmlCache[xmlPath];
            else
            {
                metadata = xmlCache[xmlPath] = new Hashtable() { { IS_VALID_KEY, false } };
                var doc = new XmlDocument();
                if (File.Exists(xmlPath))
                {
                    try { doc.Load(xmlPath); metadata[IS_VALID_KEY] = true; }
                    catch (Exception e) { Logger.Error(e.Message); }
                }
                metadata["@genre"] = GetSingle(doc, "/Title/Genres", "");
                metadata["@title"] = GetSingle(doc, "/Title/LocalTitle", "");
                metadata["@duration"] = GetSingle(doc, "/Title/RunningTime", "");
                metadata["@description"] = GetSingle(doc, "/Title/Description", "");
                metadata[IMG_PATH_KEY] = GetSingle(doc, "/Title/Covers/Front", "");
            }
            foreach (var key in metadata.Keys)
                properties[key] = metadata[key];
            return ((bool)metadata[IS_VALID_KEY]);
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
            if (entryModel.TopDirs.Count == 1)
                entryModel.SetDirectory(entryModel.TopDirs[0].Key);

            return dict;
        }


        /// <summary>
        /// Recursively watch a path for file system events.
        /// </summary>
        protected void WatcherInit(string path)
        {
            Logger.Verbose("VideosLibrary::WatcherInit; " + path);
            int idx = fileWatchers.FindIndex(x => path.Contains(x.Path));
            if (idx >= 0)
            {
                if (fileWatchers[idx].EnableRaisingEvents)
                    return;
                path = fileWatchers[idx].Path;
                fileWatchers[idx].Dispose();
                fileWatchers.RemoveAt(idx);
            }
            try { fileWatchers.Add(new FileSystemWatcher(path)); }
            catch (Exception e) { Logger.Verbose("VideosLibrary::WatcherInit; " + e.Message); return; }

            var watcher = fileWatchers.Last();
            watcher.Created += WatcherAction;
            watcher.Deleted += WatcherAction;
            watcher.Renamed += WatcherAction;
            watcher.Error += WatcherError;
            watcher.IncludeSubdirectories = true;
            watcher.EnableRaisingEvents = true;
        }


        /// <summary>
        /// Action to perform if a change is detected while watching the file system.
        /// </summary>
        protected void WatcherAction(Object sender, FileSystemEventArgs e)
        {
            Logger.Verbose("VideosLibrary::WatcherAction; " + e.ChangeType + ", " + e.FullPath);

            string baseDir = Path.GetDirectoryName(e.FullPath);
            if (e.ChangeType == WatcherChangeTypes.Created)
            {
                FileSystemInfo info = (Directory.Exists(e.FullPath)
                    ? (FileSystemInfo)new DirectoryInfo(e.FullPath)
                    : (FileSystemInfo)new FileInfo(e.FullPath));
                lock (fileSystemCache)
                    if (fileSystemCache.ContainsKey(baseDir))
                        fileSystemCache[baseDir].Add(info);
            }
            else if (e.ChangeType == WatcherChangeTypes.Deleted)
            {
                lock (fileSystemCache)
                {
                    fileSystemCache.Remove(e.FullPath);
                    if (fileSystemCache.ContainsKey(baseDir))
                        fileSystemCache[baseDir].RemoveAll(x => x.FullName == e.FullPath);
                }
            }
            else if (fileSystemCache.ContainsKey(baseDir))
            {
                var infos = new DirectoryInfo(baseDir).GetFileSystemInfos().ToList();
                lock (fileSystemCache) fileSystemCache[baseDir] = infos;
            }

            //REFRESH IMG/XML/ETC CACHE WHERE NECESSARY

            entryModel.NeedsReloading = true;
        }


        /// <summary>
        /// Action to perform if an error is encountered while watching the file system.
        /// </summary>
        protected void WatcherError(Object sender, ErrorEventArgs e)
        {
            Logger.Verbose("VideosLibrary::WatcherError; " + e.GetException());
            string path = ((FileSystemWatcher)sender).Path;
            do
            {
                try { WatcherInit(path); }
                catch { System.Threading.Thread.Sleep(5000); }
            }
            while (!fileWatchers.First(x => path.Contains(x.Path)).EnableRaisingEvents);
            lock (fileSystemCache) fileSystemCache.Clear();
            entryModel.NeedsReloading = true;
        }


        /// <summary>
        /// Assign a new ui element if null and update it based on the current skin.
        /// Returns true if the element was updated and added to the list.
        /// Parameters is updated with IS_CACHED_KEY if an image was retrieved from the cache.
        /// Parameters is updated with IS_VALID_KEY to indicate if ui is defined by the skin.
        /// </summary>
        protected bool AddUiElement(ArrayList list, Hashtable parameters, ref GBPVRUiElement ui)
        {
            string name = Convert.ToString(parameters["name"]);
            bool hasRect = skinHelper2.checkPlacementRectDefined(name);
            bool hasImg = skinHelper2.checkCompositeImageDefined(name);
            bool isValid = (hasRect && hasImg && !(ui != null && ui.alpha == 0));

            bool paramsValid = true;
            if (parameters.ContainsKey(IS_VALID_KEY))
                paramsValid = (bool)parameters[IS_VALID_KEY];
            parameters[IS_VALID_KEY] = isValid;
            parameters[IS_CACHED_KEY] = false;
            isValid &= paramsValid;

            //CHECK SKIN FOR INSET FLAG?

            if (isValid)
            {
                if (ui == null) ui = new GBPVRUiElement(name, RectangleF.Empty, null);
                ui.image = skinHelper2.getNamedImage(name, parameters);
                ui.SetRect(skinHelper2.getPlacementRect(name));
                if ((isValid = (bool)parameters[IS_VALID_KEY]))
                {
                    ui.forceRefresh = true;
                    list.Add(ui);
                }
                else ui.image = null;
                parameters[IS_VALID_KEY] = true;
            }
            return isValid;
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
                getXmlMetadata = (mode == ViewMode.MODE_DETAILS);
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
                properties.Add("@description", "");
                properties.Add("@textStyle", textStyle);
                properties.Add("@folderName", entry.Name);
                properties.Add("@fileName", entry.Name);
                properties.Add("@name", entry.Name);
                properties.Add("path", path);
                properties.Add(ENTRY_TYPE_KEY, entry.Type);
                properties.Add(imgTypes[ImageType.PREVIEW], this);
                properties.Add(imgTypes[ImageType.FILE_IMAGE], this);
                properties.Add(imgTypes[ImageType.FOLDER_IMAGE], this);
                foreach (var key in entry.Properties.Keys)
                    properties[key] = entry.Properties[key];

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
        /// Helper; gets metadata and watched status for a single entry in entryModel.
        /// </summary>
        protected void GetEntryInfo(string path, Entry entry, bool inView)
        {
            if (entry.Type == EntryType.UP)
                return;
            if (getXmlMetadata && inView && entry.Properties.Count == 0)
            {
                string xml = FindXmlFile(path, entryModel.FileExtensions, false);
                entry.NeedsRefreshing |= GetXmlMetadata(entry.Properties, xml);
            }
            if (entry.Type == EntryType.FILE || entry.Type == EntryType.DVD)
            {
                if (entry.Status == Playback.NULL &&
                    (!playbackCache.ContainsKey(path) || playbackCache[path] == Playback.NULL))
                {
                    entry.NeedsRefreshing = true;
                    entry.Status = GetPlaybackStatus(path);
                    lock (playbackCache) playbackCache[path] = entry.Status;
                }
                else entry.Status = playbackCache[path];
            }
        }


        /// <summary>
        /// Loop, getting metadata and watched status for all entries in entryModel.
        /// Passed to threads as an action.
        /// </summary>
        protected void GetEntryInfos()
        {
            var idxs = new List<int>();
            var UpdateIdxs = new Func<List<int>, bool, bool>((list, doInit) =>
            {
                bool inView = false;
                int viewCount = viewCounts[base.uiList.getViewMode()];
                int top = Math.Max(0, entryModel.BottomIndex - viewCount);
                if (doInit)
                    list.Clear();
                for (int i = 0; i < entryModel.Count && doInit; ++i)
                    list.Add(i);
                for (int i = entryModel.BottomIndex; i >= top ; --i)
                {
                    if (doInit || list.Contains(i))
                    {
                        list.Remove(i);
                        list.Insert(0, i);
                        inView = true;
                    }
                }
                return inView;
            });
            
            string path;
            while (true)
            {
                lock (entryModel)
                {
                    Monitor.Wait(entryModel);
                    path = entryModel.CurrentPath;
                    UpdateIdxs(idxs, true);
                }

                dbConnection.Open();
                while (idxs.Count > 0)
                {
                    string entryPath;
                    Entry entry;
                    bool inView;
                    lock (entryModel)
                    {
                        if (path != entryModel.CurrentPath)
                            break;
                        inView = UpdateIdxs(idxs, false);
                        entryPath = entryModel[idxs[0]].Key;
                        entry = entryModel[idxs[0]].Value;
                        idxs.RemoveAt(0);
                    }
                    GetEntryInfo(entryPath, entry, inView);
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
            ImageInfo info = null;
            while (true)
            {
                lock (imgRequests)
                {
                    int prevIdx = imgRequests.IndexOf(info);
                    while (prevIdx == imgRequests.Count - 1)
                        Monitor.Wait(imgRequests);
                    info = imgRequests[0];
                }

                Thread thread = new Thread(delegate()
                    {
                        CacheImage(info.Type, info.Paths[0], info.Width, info.Height);
                        lock (imgRequests) imgRequests.Remove(info);
                    });
                thread.Priority = ThreadPriority.Lowest;
                thread.IsBackground = true;
                thread.Start();

                string dir = entryModel.GetDirectory();
                while (dir == entryModel.GetDirectory())
                    if (thread.Join(50)) break;
            }
        }


        /// <summary>
        /// Retrieve a single image from cache/file then pulse a notification.
        /// </summary>
        protected void CacheImage(ImageType imgType, string path, int width, int height)
        {
            bool dbg = false;
            var start_ = DateTime.Now;

            //Look for a packed image before doing anything else.
            PackedImage img = null;
            string imgPath = null;
            lock (imgCache)
            {
                img = imgCache.FirstOrDefault(x => x.Tag.Paths.Contains(path));
                if (img != null)
                    imgPath = img.Tag.ImgPath;
            }

            //Determine what image, if any, to associate with the given path.
            if (img == null)
            {
                var info = GetPathInfo(path);
                bool isFile = (info is FileInfo);
                bool isDvd = Path.GetFileName(path).Equals("VIDEO_TS");
                var files = GetPathInfos(entryModel.GetDirectory()).OfType<FileInfo>();

                if (isFile && Regex.IsMatch(path, picExtensions, RegexOptions.IgnoreCase))
                    imgPath = path;
                else if (imgType == ImageType.BACKGROUND)
                    imgPath = GetCongruentFile(path, picExtensions, files);
                else
                    imgPath = FindCoverArt(path, entryModel.FileExtensions, strictFolderMetadata);

                if (imgPath == null)
                    imgPath = (isFile || isDvd ? fileImgPath : folderImgPath);
                if ((imgPath == null || imgPath == "NULL.JPG" || !GetPathInfo(imgPath).Exists))
                    imgPath = null;
            }
            if (dbg) Logger.Verbose("__isNullImg: " + (imgPath == null) + ", " + path);
            if (dbg) Logger.Verbose("__imgRequested: " + imgType + ", " + imgPath + ", " + width + "x" + height);
            if (dbg) Logger.Verbose("__findImageTime: " + DateTime.Now.Subtract(start_).TotalMilliseconds);

            //Search for a suitable match in the cache before going to disk.
            lock (imgCache)
            {
                for (int i = 0; i < imgCache.Count && img == null; ++i)
                {
                    var tagType = imgCache[i].Tag.Type;
                    string tagImgPath = imgCache[i].Tag.ImgPath;
                    if ((imgPath == null && tagType == ImageType.NULL) || imgPath == tagImgPath)
                    {
                        img = imgCache[i];
                        img.Tag.Paths.Add(path);
                        if (dbg) Logger.Verbose("__foundCached: " + path + ", " + imgPath);
                    }
                }
                if (imgPath == null && img == null)
                {
                    ImageInfo.NULL.Paths.Add(path);
                    img = new PackedImage(null, ImageInfo.NULL);
                    if (dbg) Logger.Verbose("__addingNullImg: " + path);
                }
            }

            //Load image from disk if there is no cache match.
            if (img == null)
            {
                using (var fileImg = new Bitmap(imgPath))
                using (var newImg = new Bitmap(fileImg.Width, fileImg.Height, PixelFormat.Format24bppRgb))
                {
                    //If not png, strip alpha information so the image can be more efficiently packed.
                    bool useNew = (fileImg.PixelFormat != PixelFormat.Format24bppRgb &&
                        String.Compare(Path.GetExtension(imgPath), ".png", true) != 0);
                    if (useNew)
                        using (var g = Graphics.FromImage(newImg)) g.DrawImage(fileImg, 0, 0);

                    var tag = new ImageInfo(path, imgPath, imgType, fileImg.Width, fileImg.Height);
                    img = new PackedImage((useNew ? newImg : fileImg), tag);
                    if (dbg) Logger.Verbose("__imgFromDisk: " + imgPath + ", " + path);
                }
            }
            
            //Unpack and append image to cache and then enforce cache size.
            img.UnpackImage(width, height, imgType);
            lock (imgCache)
            {
                imgCache.Remove(img);
                imgCache.Add(img);
                for (int i = 0; i < imgCache.Count - IMG_CACHE_LEN; ++i)
                {
                    if (imgCache[i].Tag.Type != ImageType.NULL &&
                        imgCache[i].Tag.ImgPath != upImgPath &&
                        imgCache[i].Tag.ImgPath != fileImgPath &&
                        imgCache[i].Tag.ImgPath != folderImgPath)
                    {
                        imgCache[i].PackImage();
                    }
                }
                Monitor.Pulse(imgCache);
                if (dbg) Logger.Verbose("__finished loading: " + imgPath + ", "
                    + width + "x" + height + ", " + img.IsPacked(width, height));
            }

            doNeedRendering = true;
        }


        /// <summary>
        /// Optionally wait some time for a cache update then search it for an image.
        /// </summary>
        protected PackedImage GetCachedImage(string path, int width, int height, TimeSpan? timeout)
        {
            PackedImage img = null;
            lock (imgCache)
            {
                if (timeout.HasValue && timeout.Value.TotalMilliseconds > 0)
                    Monitor.Wait(imgCache, timeout.Value);
                img = imgCache.FirstOrDefault(x => (x.Tag.Paths.Contains(path) &&
                    (x.Tag.Type == ImageType.NULL || !x.IsPacked(width, height))));
                if (img != null)
                {
                    imgCache.Remove(img);
                    imgCache.Add(img);
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
            IEnumerable<DirectoryInfo> dirs =
                (GetPathInfos(cwd) ?? new List<FileSystemInfo>()).OfType<DirectoryInfo>();
            IEnumerable<FileInfo> files =
                (GetPathInfos(cwd) ?? new List<FileSystemInfo>()).OfType<FileInfo>();

            foreach (var dir in dirs)
            {
                if (!IsValidDir(dir, entryModel.FileExtensions, false))
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
                    if (!IsValidPath(target, entryModel.FileExtensions, false))
                        continue;
                    bool isDirLink = Directory.Exists(target);
                    var type = (isDirLink ? EntryType.FOLDER : EntryType.FILE);
                    string name = Path.GetFileNameWithoutExtension(file.FullName) + "*";
                    var entry = new Entry() { Type = type, Name = name, Created = file.CreationTime, IsLink = true };
                    entryModel.Insert((isDirLink ? dirCount++ : entryModel.Count), target, entry);
                }
                else if (IsValidFile(file, entryModel.FileExtensions))
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
                catch (MissingMethodException) { resume = true; }
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

            if (cmd == Command.PLAY)
            {
                if (entry.Type == EntryType.DVD)
                    pluginHelper.PlayDVDFromDirectory(Path.GetDirectoryName(path));
                else if (entry.Type == EntryType.FILE && path.EndsWith(".iso"))
                    pluginHelper.PlayDVDFromISO(path);
                else if (entry.Type == EntryType.FILE)
                    PlayVideoFile(path, (entry.Status != Playback.UNWATCHED));
                else
                {
                    entryModel.NeedsReloading = true;
                    if (entry.Type == EntryType.UP)
                        lock (entryModel) entryModel.PopLevel();
                    else if (IsValidDir(path, entryModel.FileExtensions, true))
                        lock (entryModel) entryModel.PushLevel(path);
                    else entryModel.NeedsReloading = false;

                    if (entryModel.NeedsReloading)
                    {
                        PopulateListWidget();
                        WatcherInit(path);
                        lock (imgRequests) imgRequests.Clear();
                        entryModel.NeedsReloading = false;
                    }
                }
                lock (playbackCache)
                    entry.Status = playbackCache[path] = Playback.NULL;
            }
            else if (cmd == Command.MODE)
            {
                int endMode = (int)UiList.ViewMode.MODE_MMC_BUTTONS;
                int newMode = ((int)base.uiList.getViewMode() + 1) % endMode;
                var mode = (UiList.ViewMode)newMode;
                base.uiList.setViewMode(mode);
                if (mode == UiList.ViewMode.MODE_LIST)
                {
                    lock (imgRequests) imgRequests.Clear();
                    lock (imgCache) imgCache.Clear();
                    GC.Collect();
                }
                //getXmlMetadata = (mode == ViewMode.MODE_DETAILS);
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
        /// Updates parameters with IS_VALID_KEY and IS_CACHED_KEY.
        /// </summary>
        public virtual Image GetImage(Hashtable parameters, string name, int width, int height)
        {
            Logger.Verbose("VideosLibrary::GetImage; " +
                imgTypes.First(x => x.Value.Equals(name)).Key + ", " +
                parameters["path"].ToString() + ", " + width + "x" + height);

            ImageType imgType = imgTypes.First(x => x.Value.Equals(name)).Key;
            var entryType = (EntryType)(parameters.ContainsKey(ENTRY_TYPE_KEY)
                ? parameters[ENTRY_TYPE_KEY] : EntryType.NULL);
            string path = parameters["path"].ToString();

            string searchPath = path;
            if (entryType == EntryType.UP)
                searchPath = upImgPath ?? "NULL.JPG";
            else if (imgType == ImageType.BACKGROUND)
                searchPath = Path.Combine(entryModel.GetDirectory(), "BACKGROUND");
            
            PackedImage img = GetCachedImage(searchPath, width, height, null);
            if (img == null)
            {
                lock (imgRequests)
                {
                    var info = imgRequests.FirstOrDefault(x => x.Paths.Contains(searchPath));
                    if (info == null || imgType != ImageType.PREVIEW)
                    {
                        imgRequests.Remove(info);
                        info = new ImageInfo(searchPath, null, imgType, width, height);
                        imgRequests.Insert(0, info);
                        Monitor.Pulse(imgRequests);
                    }
                }
            }

            var start = DateTime.Now;
            var timeout = new TimeSpan(0, 0, 0, 0, 1);
            while (img == null && timeout.TotalMilliseconds >= 0)
            {
                img = GetCachedImage(searchPath, width, height, timeout);
                timeout = timeout.Subtract(DateTime.Now.Subtract(start));
            }

            parameters[IS_VALID_KEY] = false;
            parameters[IS_CACHED_KEY] = false;
            if (img == null)
            {
                //Workaround to bypass GBPVR image caching.
                parameters[RERENDER_KEY] = true;
                if (imgIds.ContainsKey(path))
                    parameters[path] = imgIds[path]++;
                return null;
            }
            else
            {
                bool isValid = (img.Tag.Type != ImageType.NULL);
                parameters[IS_VALID_KEY] = isValid;
                parameters[IS_CACHED_KEY] = true;
                return (isValid ? img.GetImage(width, height) : null);
            }
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
            if (!activated || entryModel.Count == 0) return list;

            var parameters = new Hashtable();
            bool isFolder = (entryModel.CurrentIndex < entryModel.GetFolderCount());

            parameters["name"] = "StatusInfo";
            double elapsed = DateTime.Now.Subtract(entryModel.LastManualSort).TotalSeconds;
            if (statusUi != null) statusUi.alpha = (int)Math.Max(0, 255 - Math.Pow(elapsed, 3));
            string sortingStr = "Sort: " + (
                (entryModel.Sorting == SortMethod.ALPHA_NUMERIC) ? "AlphaNumeric" :
                (entryModel.Sorting == SortMethod.CREATION) ? "CreationTime" :
                (entryModel.Sorting == SortMethod.SHUFFLE) ? "Shuffle" : "?");
            parameters["@" + parameters["name"]] = sortingStr;
            doNeedRendering |= AddUiElement(list, parameters, ref statusUi);
            parameters.Clear();

            parameters["name"] = (isFolder ? "FolderSummary" : "InfoSummary");
            parameters[IS_VALID_KEY] = false;
            var metadata = entryModel.CurrentEntry.Properties;
            foreach (var key in metadata.Keys)
                parameters[key] = metadata[key];
            AddUiElement(list, parameters, ref summaryUi);
            getXmlMetadata |= (bool)parameters[IS_VALID_KEY];
            parameters.Clear();

            parameters["name"] = (isFolder ? "FolderArt" : "CoverArt");
            parameters["path"] = entryModel.CurrentPath;
            parameters[imgTypes[ImageType.PREVIEW]] = this;
            parameters[ENTRY_TYPE_KEY] = entryModel.CurrentEntry.Type;
            var IsCoverStale = new Func<bool>(() =>
                (coverUi == null || !coverUi.name.EndsWith(entryModel.CurrentPath)));
            if (IsCoverStale() && (AddUiElement(list, parameters, ref coverUi) || (bool)parameters[IS_CACHED_KEY]))
                coverUi.name = parameters["name"] + "|" + entryModel.CurrentPath;
            if (!(IsCoverStale() || coverUi.image == null))
                list.Add(coverUi);
            doNeedRendering |= (IsCoverStale() && (bool)parameters[IS_VALID_KEY]);
            parameters.Clear();

            parameters["name"] = "Background";
            parameters["path"] = entryModel.GetDirectory();
            parameters[imgTypes[ImageType.BACKGROUND]] = this;
            parameters[ENTRY_TYPE_KEY] = entryModel.CurrentEntry.Type;
            var IsBgStale = new Func<bool>(() =>
                (backgroundUi == null || backgroundUi.name != entryModel.GetDirectory()));
            if (IsBgStale() && (AddUiElement(list, parameters, ref backgroundUi) || (bool)parameters[IS_CACHED_KEY]))
            {
                list.Remove(backgroundUi);
                backgroundUi.name = entryModel.GetDirectory();
            }
            if (!(IsBgStale() || backgroundUi.image == null))
            {
                //TEMP SLICK LOGIC.
                int idx = list.Cast<GBPVRUiElement>().ToList().FindIndex(x =>
                    (x.name.Contains("background"))) + 1;
                if (idx > 0) ((GBPVRUiElement)list[idx - 1]).alpha = 75;
                list.Insert(0, backgroundUi);
            }
            doNeedRendering |= (IsBgStale() && (bool)parameters[IS_VALID_KEY]);
            parameters.Clear();

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
                int viewCount = viewCounts[base.uiList.getViewMode()];
                int btm = entryModel.BottomIndex;
                int top = btm - (viewCount - 1);
                bool inView = ((top <= i && i <= btm));

                if (inView && (entry.Properties.Count == 0 || entry.Status == Playback.NULL))
                    lock (entryModel) Monitor.Pulse(entryModel);
                if (entry.NeedsRefreshing)
                {
                    entry.NeedsRefreshing = false;
                    entryModel.NeedsRefreshing = true;
                }
            }
            double elapsed = DateTime.Now.Subtract(entryModel.LastReload).TotalSeconds;
            bool isShuffled = (entryModel.Sorting == SortMethod.SHUFFLE);
            entryModel.NeedsReloading |= (elapsed > RELOAD_TIMEOUT && !isShuffled);
            //REFRESH FILESYSTEM CACHE IF TIMEOUT

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
                lock (playbackCache)
                {
                    foreach (var pair in entryModel)
                    {
                        string path = pair.Key;
                        var entry = pair.Value;
                        if (playbackCache.ContainsKey(path))
                            entry.Status = playbackCache[path];
                    }
                }
            }

            //Finish with assignments to the base class.
            base.uiList.setViewMode(UpdatedViewMode());
            base.uiList.setItemList(UpdatedUiList());
            if (entryModel.Count > 0)
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

            if (mapping.Equals("LIBRARY_FASTFORWARD") || mapping.Equals("LIBRARY_SKIP_FORWARDS"))
            {
                if (entryModel.Count > 0)
                    SelectedItem(base.uiList.getItemList()[entryModel.Count - 1]);
            }
            else if (mapping.Equals("LIBRARY_REWIND") || mapping.Equals("LIBRARY_SKIP_BACKWARDS"))
            {
                if (entryModel.Count > 0)
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
            skinHelper2 = new SkinHelper2(Path.Combine(getSkinSubdirectory(), "skin.xml"));

            //Initialize class variables.
            imgRequests = new List<ImageInfo>();
            imgCache = new List<PackedImage>();
            imgIds = new Dictionary<string, int>();
            imgTypes = new Dictionary<ImageType, string>()
            {
                { ImageType.BACKGROUND, "@background" },
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
            string skinRoot = pluginHelper.GetSkinRootDirectory().Replace(@"\skin", @"\skin2");
            string imgPath = Path.Combine(skinRoot, "_CoreImages");
            upImgPath = Path.Combine(imgPath, "Folder-Up.png");
            fileImgPath = Path.Combine(imgPath, "Video-File.png");
            folderImgPath = Path.Combine(imgPath, "Video-Folder.png");
            if (!File.Exists(upImgPath)) upImgPath = null;
            if (!File.Exists(fileImgPath)) fileImgPath = null;
            if (!File.Exists(folderImgPath)) folderImgPath = null;

            //Assign configuration values to both member variables and options map.
            bool slick = Regex.IsMatch(skinRoot, @"skin2\\slick", RegexOptions.IgnoreCase);
            options = InitOptions(pluginHelper.GetConfiguration());
            options["ShowMode"] = showMode = (showMode && !slick);

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

            //Start a thread to retrieve metadata and database info.
            var dbThread = new Thread(GetEntryInfos);
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
