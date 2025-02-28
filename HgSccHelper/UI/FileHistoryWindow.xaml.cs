﻿//=========================================================================
// Copyright 2009 Sergey Antonov <sergant_@mail.ru>
//
// This software may be used and distributed according to the terms of the
// GNU General Public License version 2 as published by the Free Software
// Foundation.
//
// See the file COPYING.TXT for the full text of the license, or see
// http://www.gnu.org/licenses/gpl-2.0.txt
//
//=========================================================================

using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.Generic;
using System;
using System.Windows.Data;
using System.Windows.Threading;
using HgSccHelper.CommandServer;
using HgSccHelper.UI;

namespace HgSccHelper
{
	public partial class FileHistoryWindow : Window
	{
		//-----------------------------------------------------------------------------
		public string WorkingDir { get; set; }

		//------------------------------------------------------------------
		public string FileName { get; set; }

		//------------------------------------------------------------------
		public string Rev { get; set; }

		//------------------------------------------------------------------
		public UpdateContext UpdateContext { get; private set; }

		//------------------------------------------------------------------
		HgClient HgClient { get { return UpdateContext.Cache.HgClient; } }

		//------------------------------------------------------------------
		ParentsInfo ParentsInfo { get; set; }

		//------------------------------------------------------------------
		SelectedParentFile SelectedParentFile { get; set; }

		//------------------------------------------------------------------
		/// <summary>
		/// SHA1 -> BranchInfo map
		/// </summary>
		Dictionary<string, BranchInfo> Branches { get; set; }

		//------------------------------------------------------------------
		/// <summary>
		/// Tag Name -> TagInfo map
		/// </summary>
		Dictionary<string, TagInfo> Tags { get; set; }

		//------------------------------------------------------------------
		/// <summary>
		/// SHA1 -> FileHistoryInfo2 map
		/// </summary>
		Dictionary<string, FileHistoryInfo2> file_history_map;

		//------------------------------------------------------------------
		/// <summary>
		/// Bookmark Name -> BookmarkInfo map
		/// </summary>
		Dictionary<string, BookmarkInfo> Bookmarks { get; set; }

		//-----------------------------------------------------------------------------
		private AsyncOperations async_ops;

		//-----------------------------------------------------------------------------
		private Cursor prev_cursor;

		Dictionary<ListView, GridViewColumnSorter> files_sorter;

		private DispatcherTimer timer;

		public const string CfgPath = @"GUI\FileHistoryWindow";
		CfgWindowPosition wnd_cfg;
		private System.Text.StringBuilder sb = new StringBuilder();

		//------------------------------------------------------------------
		public FileHistoryWindow()
		{
			wnd_cfg = new CfgWindowPosition(CfgPath, this);

			InitializeComponent();

			HgSccHelper.UI.ThemeManager.Instance.Subscribe(this);

			UpdateContext = new UpdateContext();
			file_history_map = new Dictionary<string, FileHistoryInfo2>();

			files_sorter = new Dictionary<ListView, GridViewColumnSorter>();
			diffColorizer.Complete = new Action<List<string>>(OnDiffColorizer);

			timer = new DispatcherTimer();
			timer.Interval = TimeSpan.FromMilliseconds(50);
			timer.Tick += TimerOnTick;
		}

		//-----------------------------------------------------------------------------
		private AsyncOperations RunningOperations
		{
			get { return async_ops; }
			set
			{
				if (async_ops != value)
				{
					if (async_ops == AsyncOperations.None)
					{
						prev_cursor = Cursor;
						Cursor = Cursors.Wait;
					}

					async_ops = value;

					if (async_ops == AsyncOperations.None)
					{
						Cursor = prev_cursor;
					}
				}
			}
		}

		//-----------------------------------------------------------------------------
		private void OnDiffColorizer(List<string> obj)
		{
			RunningOperations &= ~AsyncOperations.Diff;
		}

		//------------------------------------------------------------------
		private void Window_Loaded(object sender, RoutedEventArgs e)
		{
			listChangesGrid.LoadCfg(FileHistoryWindow.CfgPath, "ListChangesGrid");
			
			int diff_width;
			Cfg.Get(CfgPath, DiffColorizerControl.DiffWidth, out diff_width, DiffColorizerControl.DefaultWidth);
			diffColorizer.Width = diff_width;

			int diff_visible;
			Cfg.Get(CfgPath, DiffColorizerControl.DiffVisible, out diff_visible, 1);
			expanderDiff.IsExpanded = (diff_visible != 0);

			Tags = new Dictionary<string, TagInfo>();
			Branches = new Dictionary<string, BranchInfo>();
			Bookmarks = new Dictionary<string, BookmarkInfo>();

			var files = HgClient.StatusChange(FileName, Rev ?? "");
			if (files.Count == 1
				&& files[0].Status == HgFileStatus.Added
				&& files[0].CopiedFrom != null)
			{
				var file_info = files[0];
				FileName = file_info.CopiedFrom;
			}

			Title = string.Format("File History: '{0}'", FileName);

			var tt = Stopwatch.StartNew();
			var rename_parts = TrackRenames(HgClient, FileName, Rev ?? "");

			sb.AppendFormat("Custom, time = {0} ms\n", tt.ElapsedMilliseconds);
			OnAsyncChangeDescFull(rename_parts);
		}

		//-----------------------------------------------------------------------------
		internal static List<RenameParts> TrackRenames(HgClient hg_client, string filename, string starting_revision)
		{
			// FIXME: In mercurial up to 2.1 there is no way to get a history of the file
			// with following copies/renames starting from arbitrary revision.
			// The follow only works from working directory parent.

			// So, use a version without rename tracking if the starting revision is not empty

			string rev_range = "";
			bool follow = true;
			if (!String.IsNullOrEmpty(starting_revision))
			{
				rev_range = String.Format("reverse(::{0})", starting_revision);
				follow = false;
			}

			var follow_revs = hg_client.RevLogPath(filename, rev_range, 0, follow);

			var parts = new List<RenameParts>();
			filename = filename.Replace('/', '\\');

			if (!follow)
			{
				if (follow_revs.Count > 0)
					parts.Add(new RenameParts { FileName = filename, Revs = follow_revs });

				return parts;
			}

			// Map for filenames -> sha1 revlist without follow
			var file_to_revs = new Dictionary<string, HashSet<string>>();

			while (follow_revs.Count > 0)
			{
				var current = follow_revs[0];

				HashSet<string> no_follow;
				if (!file_to_revs.TryGetValue(filename, out no_follow))
				{
					var no_follow_list = hg_client.RevLogPathSHA1(filename,
						String.Format("{0}:0", current.SHA1),
						0, false);

					no_follow = new HashSet<string>(no_follow_list);
					file_to_revs.Add(filename, no_follow);
				}

				int mismatch_idx = -1;

				for (int i = 0; i < follow_revs.Count; ++i)
				{
					if (!no_follow.Contains(follow_revs[i].SHA1))
					{
						mismatch_idx = i;
						break;
					}

					no_follow.Remove(follow_revs[i].SHA1);
				}

				if (mismatch_idx == -1)
				{
					// No more renames
					mismatch_idx = follow_revs.Count;
				}

				if (mismatch_idx == 0)
				{
					// This should not happen
					break;
				}

				var last_rev = follow_revs[mismatch_idx - 1];

				parts.Add(new RenameParts { FileName = filename, Revs = follow_revs.GetRange(0, mismatch_idx) });
				follow_revs.RemoveRange(0, mismatch_idx);

				if (!hg_client.TrackRename(filename, last_rev.SHA1, out filename))
					break;

				// FIXME: TrackRename returns filename with '/' separator
				filename = filename.Replace('/', '\\');
			}

			return parts;
		}

		//------------------------------------------------------------------
		private void Window_Closed(object sender, EventArgs e)
		{
			timer.Stop();
			timer.Tick -= TimerOnTick;

			diffColorizer.Dispose();

			Cfg.Set(CfgPath, DiffColorizerControl.DiffVisible, expanderDiff.IsExpanded ? 1 : 0);
			if (!Double.IsNaN(diffColorizer.Width))
			{
				int diff_width = (int)diffColorizer.Width;
				if (diff_width > 0)
					Cfg.Set(CfgPath, DiffColorizerControl.DiffWidth, diff_width);
			}

			listChangesGrid.SaveCfg(FileHistoryWindow.CfgPath, "ListChangesGrid");
		}

		//-----------------------------------------------------------------------------
		private void OnAsyncChangeDescFull(List<RenameParts> parts)
		{
			if (parts.Count == 0)
			{
				Close();
				return;
			}

			var t2 = Stopwatch.StartNew();
			var history = new List<FileHistoryInfo2>();

			int part_idx = 0;
			foreach (var part in parts)
			{
				part_idx++;

				foreach (var change_desc in part.Revs)
				{
					var history_item = new FileHistoryInfo2();
					history_item.ChangeDesc = change_desc;
					history_item.FileName = part.FileName;
					history_item.GroupText = String.Format("[{0}]: {1}", part_idx, part.FileName);

					if (ParentsInfo != null)
					{
						foreach (var parent in ParentsInfo.Parents)
						{
							if (history_item.ChangeDesc.SHA1 == parent.SHA1)
							{
								history_item.IsCurrent = true;
								break;
							}
						}
					}

					BranchInfo branch_info;
					if (Branches.TryGetValue(history_item.ChangeDesc.SHA1, out branch_info))
						history_item.BranchInfo = branch_info;

					file_history_map[history_item.ChangeDesc.SHA1] = history_item;
					history.Add(history_item);
				}
			}

			sb.AppendFormat("Build history {0} ms\n", t2.ElapsedMilliseconds);

			var t3 = Stopwatch.StartNew();
			listChanges.ItemsSource = history;
			if (listChanges.Items.Count > 0)
				listChanges.SelectedIndex = 0;

			listChanges.Focus();
			sb.AppendFormat("Binding {0} ms\n", t3.ElapsedMilliseconds);

			var t4 = Stopwatch.StartNew();

			if (UpdateContext.Cache.Branches != null)
				OnAsyncBranch(UpdateContext.Cache.Branches);
			else
				HandleBranchChanges();

			if (UpdateContext.Cache.Tags != null)
				OnAsyncTags(UpdateContext.Cache.Tags);
			else
				HandleTagsChanges();

			if (UpdateContext.Cache.ParentsInfo != null)
				OnAsyncParents(UpdateContext.Cache.ParentsInfo);
			else
				HandleParentChange();

			if (UpdateContext.Cache.Bookmarks != null)
				OnAsyncBookmarks(UpdateContext.Cache.Bookmarks);
			else
				HandleBookmarksChanges();

			if (parts.Count > 1)
			{
				// Since grouping is effectively disable virtualization,
				// enable it only if there were file renames

				listChanges.GroupStyle.Clear();
				listChanges.GroupStyle.Add((GroupStyle)Resources["GroupStyleForRenames"]);

				var myView = (CollectionView)CollectionViewSource.GetDefaultView(listChanges.ItemsSource);
				var groupDescription = new PropertyGroupDescription("GroupText");
				myView.GroupDescriptions.Clear();
				myView.GroupDescriptions.Add(groupDescription);
			}

			sb.AppendFormat("Other {0} ms\n", t4.ElapsedMilliseconds);

			Logger.WriteLine("FileHistory Times:\n{0}\n", sb.ToString());
		}

		//-----------------------------------------------------------------------------
		private void OnAsyncTags(List<TagInfo> tags_list)
		{
			RunningOperations &= ~AsyncOperations.Tags;

			if (tags_list == null)
				return;

			var new_tags = new Dictionary<string, TagInfo>();

			foreach (var tag in tags_list)
			{
				new_tags[tag.Name] = tag;
			}

			foreach (var tag in Tags.Values)
			{
				// removing old tags
				FileHistoryInfo2 file_history;
				if (file_history_map.TryGetValue(tag.SHA1, out file_history))
				{
					var change_desc = file_history.ChangeDesc;
					var tag_name = tag.Name;
					var tag_info = change_desc.Tags.FirstOrDefault(t => t.Name == tag_name);
					if (tag_info != null)
						change_desc.Tags.Remove(tag_info);
				}
			}

			Tags = new_tags;

			foreach (var tag in Tags.Values)
			{
				// adding or updating tags
				FileHistoryInfo2 file_history;
				if (file_history_map.TryGetValue(tag.SHA1, out file_history))
				{
					var change_desc = file_history.ChangeDesc;
					var tag_name = tag.Name;

					int pos = change_desc.Tags.FirstIndexOf(t => t.Name == tag_name);
					if (pos != -1)
						change_desc.Tags[pos] = tag;
					else
						change_desc.Tags.Add(tag);
				}
			}
		}

		//-----------------------------------------------------------------------------
		private void OnAsyncBookmarks(List<BookmarkInfo> bookmarks_list)
		{
			RunningOperations &= ~AsyncOperations.Bookmarks;

			if (bookmarks_list == null)
				return;

			var new_bookmarks = new Dictionary<string, BookmarkInfo>();

			foreach (var bookmark in bookmarks_list)
			{
				new_bookmarks[bookmark.Name] = bookmark;
			}

			foreach (var bookmark in Bookmarks.Values)
			{
				// removing old bookmark
				FileHistoryInfo2 file_history;
				if (file_history_map.TryGetValue(bookmark.SHA1, out file_history))
				{
					var change_desc = file_history.ChangeDesc;
					var book_name = bookmark.Name;
					var book = change_desc.Bookmarks.FirstOrDefault(b => b.Name == book_name);
					if (book != null)
						change_desc.Bookmarks.Remove(book);
				}
			}

			Bookmarks = new_bookmarks;

			foreach (var bookmark in Bookmarks.Values)
			{
				// adding or updating bookmarks
				FileHistoryInfo2 file_history;
				if (file_history_map.TryGetValue(bookmark.SHA1, out file_history))
				{
					var change_desc = file_history.ChangeDesc;
					var book_name = bookmark.Name;

					int pos = change_desc.Bookmarks.FirstIndexOf(b => b.Name == book_name);
					if (pos != -1)
						change_desc.Bookmarks[pos] = bookmark;
					else
						change_desc.Bookmarks.Add(bookmark);
				}
			}
		}

		//-----------------------------------------------------------------------------
		private void OnAsyncBranch(List<BranchInfo> branch_list)
		{
			RunningOperations &= ~AsyncOperations.Branches;

			if (branch_list == null)
				return;

			var new_branches = new Dictionary<string, BranchInfo>();

			foreach (var branch_info in branch_list)
			{
				new_branches[branch_info.SHA1] = branch_info;
				Branches.Remove(branch_info.SHA1);
			}

			foreach (var branch_info in Branches.Values)
			{
				// removing old branch info
				FileHistoryInfo2 file_history;
				if (file_history_map.TryGetValue(branch_info.SHA1, out file_history))
					file_history.BranchInfo = null;
			}

			Branches = new_branches;

			foreach (var branch_info in Branches.Values)
			{
				// adding or updating branch info
				FileHistoryInfo2 file_history;
				if (file_history_map.TryGetValue(branch_info.SHA1, out file_history))
					file_history.BranchInfo = branch_info;
			}
		}

		//-----------------------------------------------------------------------------
		private void OnAsyncParents(ParentsInfo new_current)
		{
			RunningOperations &= ~AsyncOperations.Parents;

			if (new_current == null)
				return;

			if (ParentsInfo != null)
			{
				foreach (var parent in ParentsInfo.Parents)
				{
					FileHistoryInfo2 file_history;
					if (file_history_map.TryGetValue(parent.SHA1, out file_history))
						file_history.IsCurrent = false;
				}
			}

			ParentsInfo = new_current;
			if (ParentsInfo != null)
			{
				foreach (var parent in ParentsInfo.Parents)
				{
					FileHistoryInfo2 file_history;
					if (file_history_map.TryGetValue(parent.SHA1, out file_history))
						file_history.IsCurrent = true;
				}
			}
		}

		//-----------------------------------------------------------------------------
		private void ShowFileDiff()
		{
			if (diffColorizer == null)
				return;

			if (!expanderDiff.IsExpanded)
				return;

			diffColorizer.Clear();
			var parent_diff = GetSelectedParentDiff();
			var file_history = (FileHistoryInfo2)listChanges.SelectedItem;

			RunningOperations |= AsyncOperations.Diff;

			if (SelectedParentFile != null)
			{
				diffColorizer.RunHgDiffAsync(HgClient, SelectedParentFile.FileInfo.File,
					parent_diff.Desc.SHA1,
					file_history.ChangeDesc.SHA1);
			}
		}

		//-----------------------------------------------------------------------------
		private ParentFilesDiff GetSelectedParentDiff()
		{
			var tab = tabParentsDiff.SelectedItem as TabItem;
			if (tab == null)
				return null;

			return tab.DataContext as ParentFilesDiff;
		}

		//------------------------------------------------------------------
		private void DiffGridSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
		{
			if (diffColorizer.Width > e.HorizontalChange)
				diffColorizer.Width -= e.HorizontalChange;
			else
				diffColorizer.Width = 0;
		}

		//-----------------------------------------------------------------------------
		private void expanderDiff_Expanded(object sender, RoutedEventArgs e)
		{
			ShowFileDiff();
		}

		//-----------------------------------------------------------------------------
		private void expanderDiff_Collapsed(object sender, RoutedEventArgs e)
		{
			diffColumn.Width = new GridLength(0, GridUnitType.Auto);
			diffColorizer.Clear();
		}

		//------------------------------------------------------------------
		private void HistoryDiffPrevious_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = false;
			if (listChanges.SelectedItems.Count == 1)
			{
				if (listChanges.SelectedIndex != (listChanges.Items.Count - 1))
					e.CanExecute = true;
			}
			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void HistoryDiffPrevious_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var f1 = (FileHistoryInfo2)listChanges.Items[listChanges.SelectedIndex];
			var f2 = (FileHistoryInfo2)listChanges.Items[listChanges.SelectedIndex + 1];

			DiffTwoRevisions(f1, f2);

			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void DiffTwoRevisions(FileHistoryInfo2 f1, FileHistoryInfo2 f2)
		{
			if (f1.ChangeDesc.Rev > f2.ChangeDesc.Rev)
			{
				var temp = f2;
				f2 = f1;
				f1 = temp;
			}

			try
			{
				HgClient.Diff(f1.FileName, f1.ChangeDesc.Rev, f2.FileName, f2.ChangeDesc.Rev);
			}
			catch (HgDiffException)
			{
				Util.HandleHgDiffException();
			}
		}

		//------------------------------------------------------------------
		private void ListChanges_MouseDoubleClick(object sender, MouseEventArgs e)
		{
			if (Commands.DiffPreviousCommand != null)
			{
				if (Commands.DiffPreviousCommand.CanExecute(sender, e.Source as IInputElement))
					Commands.DiffPreviousCommand.Execute(sender, e.Source as IInputElement);
			}
		}

		//------------------------------------------------------------------
		private void FilesDiffPrevious_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = false;
			if (SelectedParentFile != null)
			{
				if (SelectedParentFile.FileInfo.Status == HgFileStatus.Added
					&& !String.IsNullOrEmpty(SelectedParentFile.FileInfo.CopiedFrom))
				{
					e.CanExecute = true;
				}

				if (SelectedParentFile.FileInfo.Status == HgFileStatus.Modified)
					e.CanExecute = true;
			}
			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void FilesDiffPrevious_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var file_history = (FileHistoryInfo2)listChanges.SelectedItem;
			var parent_diff = GetSelectedParentDiff();
			var file_info = SelectedParentFile.FileInfo;

			try
			{
				var child_file = file_info.File;
				var parent_file = file_info.File;

				if (!String.IsNullOrEmpty(file_info.CopiedFrom))
					parent_file = file_info.CopiedFrom;

				HgClient.Diff(parent_file, parent_diff.Desc.SHA1,
					child_file, file_history.ChangeDesc.SHA1);
			}
			catch (HgDiffException)
			{
				Util.HandleHgDiffException();
			}

			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void ListViewFiles_MouseDoubleClick(object sender, MouseEventArgs e)
		{
			if (Commands.DiffPreviousCommand != null)
			{
				if (Commands.DiffPreviousCommand.CanExecute(sender, e.Source as IInputElement))
					Commands.DiffPreviousCommand.Execute(sender, e.Source as IInputElement);
			}
		}

		//------------------------------------------------------------------
		private UpdateContextCache BuildUpdateContextCache()
		{
			var cache = new UpdateContextCache();
			cache.HgClient = UpdateContext.Cache.HgClient;

			if ((RunningOperations & AsyncOperations.Parents) != AsyncOperations.Parents)
				cache.ParentsInfo = ParentsInfo;

			if ((RunningOperations & AsyncOperations.Tags) != AsyncOperations.Tags)
				cache.Tags = new List<TagInfo>(Tags.Values);

			if ((RunningOperations & AsyncOperations.Branches) != AsyncOperations.Branches)
				cache.Branches = new List<BranchInfo>(Branches.Values);

			if ((RunningOperations & AsyncOperations.Bookmarks) != AsyncOperations.Bookmarks)
				cache.Bookmarks = new List<BookmarkInfo>(Bookmarks.Values);

			return cache;
		}

		//------------------------------------------------------------------
		private void FileHistory_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = SelectedParentFile != null;
			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void FileHistory_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var file_history = (FileHistoryInfo2)listChanges.SelectedItem;
			var cs = file_history.ChangeDesc;
			var rev = cs.SHA1;

			if (cs.Parents.Count == 2)
			{
				var parents = new[]
				              	{
				              		tab1.DataContext as ParentFilesDiff,
				              		tab2.DataContext as ParentFilesDiff
				              	};
				var other_parent = parents.FirstOrDefault(p => p != SelectedParentFile.ParentFilesDiff);
				if (other_parent != null)
				{
					var other_file = other_parent.Files.FirstOrDefault(f => f.FileInfo.File == SelectedParentFile.FileInfo.File);

					if (other_file != null)
					{
						rev = SelectedParentFile.ParentFilesDiff.Desc.SHA1;
						Logger.WriteLine("Found a file in both parents: {0}", SelectedParentFile.FileInfo.File);
					}
				}
			}


			var wnd = new FileHistoryWindow();
			wnd.WorkingDir = WorkingDir;
			wnd.Rev = rev;
			wnd.FileName = SelectedParentFile.FileInfo.File;

			wnd.UpdateContext.Cache = BuildUpdateContextCache();

			// FIXME:
			wnd.Owner = Window.GetWindow(this);

			wnd.ShowDialog();

			if (wnd.UpdateContext.IsParentChanged)
				HandleParentChange();

			if (wnd.UpdateContext.IsBranchChanged)
				HandleBranchChanges();

			if (wnd.UpdateContext.IsTagsChanged)
				HandleTagsChanges();

			if (wnd.UpdateContext.IsBookmarksChanged)
				HandleBookmarksChanges();

			UpdateContext.MergeWith(wnd.UpdateContext);
		}

		//------------------------------------------------------------------
		private void Annotate_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = SelectedParentFile != null;
			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void Annotate_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var file_history = (FileHistoryInfo2)listChanges.SelectedItem;
			var cs = file_history.ChangeDesc;
			var rev = cs.SHA1;

			if (cs.Parents.Count == 2)
			{
				var parents = new[]
				              	{
				              		tab1.DataContext as ParentFilesDiff,
				              		tab2.DataContext as ParentFilesDiff
				              	};
				var other_parent = parents.FirstOrDefault(p => p != SelectedParentFile.ParentFilesDiff);
				if (other_parent != null)
				{
					var other_file = other_parent.Files.FirstOrDefault(f => f.FileInfo.File == SelectedParentFile.FileInfo.File);

					if (other_file != null)
					{
						rev = SelectedParentFile.ParentFilesDiff.Desc.SHA1;
						Logger.WriteLine("Found a file in both parents: {0}", SelectedParentFile.FileInfo.File);
					}
				}
			}

			var wnd = new AnnotateWindow();
			wnd.WorkingDir = WorkingDir;
			wnd.Rev = rev;
			wnd.FileName = SelectedParentFile.FileInfo.File;

			wnd.UpdateContext.Cache = BuildUpdateContextCache();

			wnd.Owner = Window.GetWindow(this);

			wnd.ShowDialog();

			if (wnd.UpdateContext.IsParentChanged)
				HandleParentChange();

			if (wnd.UpdateContext.IsBranchChanged)
				HandleBranchChanges();

			if (wnd.UpdateContext.IsTagsChanged)
				HandleTagsChanges();

			if (wnd.UpdateContext.IsBookmarksChanged)
				HandleBookmarksChanges();

			UpdateContext.MergeWith(wnd.UpdateContext);
		}

		//------------------------------------------------------------------
		private void ViewFile_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = SelectedParentFile != null;
			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void ViewFile_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var file_history = (FileHistoryInfo2)listChanges.SelectedItem;
			var file_info = SelectedParentFile.FileInfo;
			var cs = file_history.ChangeDesc;

			if (file_info.Status == HgFileStatus.Removed)
				HgClient.ViewFile(file_info.File, SelectedParentFile.ParentFilesDiff.Desc.Rev.ToString());
			else
				HgClient.ViewFile(file_info.File, cs.Rev.ToString());
		}

		//------------------------------------------------------------------
		private void HistoryViewFile_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = SelectedParentFile != null;
			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void HistoryViewFile_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var file_history = (FileHistoryInfo2)listChanges.SelectedItem;
			var cs = file_history.ChangeDesc;

			HgClient.ViewFile(file_history.FileName, cs.Rev.ToString());
		}

		//------------------------------------------------------------------
		private void HistoryDiffTwoRevisions_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = (listChanges.SelectedItems.Count == 2);
			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void HistoryDiffTwoRevisions_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var f1 = (FileHistoryInfo2)listChanges.SelectedItems[0];
			var f2 = (FileHistoryInfo2)listChanges.SelectedItems[1];

			DiffTwoRevisions(f1, f2);

			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void Update_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = false;

			if (listChanges != null)
			{
				if (listChanges.SelectedItems.Count == 1)
				{
					var change = listChanges.SelectedItems[0] as FileHistoryInfo2;
					if (change != null)
						e.CanExecute = true;
				}
			}

			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void Update_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var change = (FileHistoryInfo2)listChanges.SelectedItems[0];

			var wnd = new UpdateWindow();
			wnd.WorkingDir = WorkingDir;
			wnd.TargetRevision = change.ChangeDesc.Rev.ToString();

			wnd.UpdateContext.Cache = BuildUpdateContextCache();

			wnd.Owner = Window.GetWindow(this);
			wnd.ShowDialog();

			if (wnd.UpdateContext.IsParentChanged)
				HandleParentChange();

			if (wnd.UpdateContext.IsBookmarksChanged)
				HandleBookmarksChanges();

			UpdateContext.MergeWith(wnd.UpdateContext);
		}

		//------------------------------------------------------------------
		private void Grep_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = true;
			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void Grep_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var wnd = new GrepWindow();
			wnd.WorkingDir = WorkingDir;

			wnd.UpdateContext.Cache = BuildUpdateContextCache();

			wnd.Owner = Window.GetWindow(this);
			wnd.ShowDialog();

			if (wnd.UpdateContext.IsParentChanged)
				HandleParentChange();

			if (wnd.UpdateContext.IsBookmarksChanged)
				HandleBookmarksChanges();

			UpdateContext.MergeWith(wnd.UpdateContext);
		}

		//-----------------------------------------------------------------------------
		private void Archive_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = false;

			if (listChanges != null)
			{
				if (listChanges.SelectedItems.Count == 1)
				{
					var change = listChanges.SelectedItems[0] as FileHistoryInfo2;
					if (change != null)
						e.CanExecute = true;
				}
			}

			e.Handled = true;
		}

		//-----------------------------------------------------------------------------
		private void Archive_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var change = (FileHistoryInfo2)listChanges.SelectedItems[0];

			var wnd = new ArchiveWindow();
			wnd.WorkingDir = WorkingDir;
			wnd.ArchiveRevision = change.ChangeDesc.Rev.ToString();

			wnd.UpdateContextCache = BuildUpdateContextCache();

			wnd.Owner = Window.GetWindow(this);
			wnd.ShowDialog();
		}


		//------------------------------------------------------------------
		private void Tags_CanExecute(object sender, CanExecuteRoutedEventArgs e)
		{
			e.CanExecute = false;

			if (listChanges != null)
			{
				if (listChanges.SelectedItems.Count == 1)
				{
					var change = listChanges.SelectedItems[0] as FileHistoryInfo2;
					if (change != null)
						e.CanExecute = true;
				}
			}

			e.Handled = true;
		}

		//------------------------------------------------------------------
		private void Tags_Executed(object sender, ExecutedRoutedEventArgs e)
		{
			var change = (FileHistoryInfo2)listChanges.SelectedItems[0];

			TagsWindow wnd = new TagsWindow();
			wnd.WorkingDir = WorkingDir;
			wnd.TargetRevision = change.ChangeDesc.Rev.ToString();

			wnd.UpdateContext.Cache = BuildUpdateContextCache();

			wnd.Owner = Window.GetWindow(this);
			wnd.ShowDialog();

			if (wnd.UpdateContext.IsParentChanged)
				HandleParentChange();

			if (wnd.UpdateContext.IsBranchChanged)
				HandleBranchChanges();

			if (wnd.UpdateContext.IsTagsChanged)
				HandleTagsChanges();

			if (wnd.UpdateContext.IsBookmarksChanged)
				HandleBookmarksChanges();

			UpdateContext.MergeWith(wnd.UpdateContext);
		}

		//------------------------------------------------------------------
		private void HandleParentChange()
		{
			RunningOperations |= AsyncOperations.Parents;
			var parents = UpdateContext.Cache.HgClient.Parents();
			OnAsyncParents(parents);
		}

		//------------------------------------------------------------------
		private void HandleBranchChanges()
		{
			RunningOperations |= AsyncOperations.Branches;
			var branches = UpdateContext.Cache.HgClient.Branches(HgBranchesOptions.Closed);
			OnAsyncBranch(branches);
		}

		//------------------------------------------------------------------
		private void HandleTagsChanges()
		{
			RunningOperations |= AsyncOperations.Tags;
			var tags = UpdateContext.Cache.HgClient.Tags();
			OnAsyncTags(tags);
		}

		//------------------------------------------------------------------
		private void HandleBookmarksChanges()
		{
			RunningOperations |= AsyncOperations.Bookmarks;
			var books = UpdateContext.Cache.HgClient.Bookmarks();
			OnAsyncBookmarks(books);
		}

		//------------------------------------------------------------------
		private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Escape)
				Close();
		}

		//-----------------------------------------------------------------------------
		private void TimerOnTick(object sender, EventArgs event_args)
		{
			RunningOperations &= ~AsyncOperations.ChangeDesc;
			timer.Stop();

			if (listChanges.SelectedItems.Count == 1)
			{
				var file_history = (FileHistoryInfo2)listChanges.SelectedItem;
				var options = HgStatusOptions.Added | HgStatusOptions.Deleted
					| HgStatusOptions.Modified
					| HgStatusOptions.Copies | HgStatusOptions.Removed;

				var parents = file_history.ChangeDesc.Parents;
				if (parents.Count == 0)
					parents = new ObservableCollection<string>(new[] { "null" });

				var parents_diff = new List<ParentFilesDiff>();

				foreach (var parent in parents)
				{
					var files = HgClient.Status(options, "", parent, file_history.ChangeDesc.SHA1);

					var desc = HgClient.GetRevisionDesc(parent);
					if (desc != null)
					{
						var parent_diff = new ParentFilesDiff();
						parent_diff.Desc = desc;
						parent_diff.Files = new List<ParentDiffHgFileInfo>();

						foreach (var file in files)
							parent_diff.Files.Add(new ParentDiffHgFileInfo { FileInfo = file });

						parents_diff.Add(parent_diff);
					}
				}

				var tabs = new[] {tab1, tab2};
				var lists = new[] { tabList1, tabList2 };
				for (int i = parents.Count; i < tabs.Length; ++i)
				{
					tabs[i].Visibility = Visibility.Collapsed;
					tabs[i].DataContext = null;
				}

				for (int i = 0; i < parents_diff.Count; ++i)
				{
					var tab = tabs[i];
					var list = lists[i];
					var parent = parents_diff[i];
					if (i == 0)
						parent.IsSelected = true;

					tab.DataContext = parent;
					tab.Visibility = Visibility.Visible;

					var file = parent.Files.FirstOrDefault(f => f.FileInfo.File == file_history.FileName);

					if (file != null)
					{
						file.IsSelected = true;
					}
					else
					{
						if (parent.Files.Count > 0)
						{
							parent.Files[0].IsSelected = true;
							file = parent.Files[0];
						}
					}

					if (file != null)
					{
						Logger.WriteLine("Scrolling list {0} to item: {1}", parent.Desc.Rev, file.FileInfo.File);
						list.ScrollIntoView(file);
					}
				}
			}
		}

		//------------------------------------------------------------------
		private void listChanges_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
		{
			tab1.DataContext = null;
			tab2.DataContext = null;
			tab2.Visibility = Visibility.Collapsed;
			tab1.Visibility = Visibility.Collapsed;
			diffColorizer.Clear();

			timer.Stop();

			if (listChanges.SelectedItems.Count == 1)
			{
				RunningOperations |= AsyncOperations.ChangeDesc;
				timer.Start();
			}
		}

		//------------------------------------------------------------------
		private void GridSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
		{
			textChangeDesc.Height = changeDescRow.Height.Value;
		}

		//------------------------------------------------------------------
		void GridViewColumnHeaderClickedHandler(object sender,
												RoutedEventArgs e)
		{
			GridViewColumnSorter column_sorter;
			ListView list_view = sender as ListView;
			if (list_view != null)
			{
				if (!files_sorter.TryGetValue(list_view, out column_sorter))
				{
					column_sorter = new GridViewColumnSorter(list_view);
					files_sorter[list_view] = column_sorter;
				}

				column_sorter.GridViewColumnHeaderClickedHandler(sender, e);
			}
		}

		//-----------------------------------------------------------------------------
		private void listViewFiles_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
		{
			SelectedParentFile = null;
			diffColorizer.Clear();

			var parent_diff = GetSelectedParentDiff();
			var list_view = e.OriginalSource as ListView;

			// FIXME: Virtualized list view does not work properly with IsSelected property binding
			foreach (ParentDiffHgFileInfo info in e.RemovedItems)
				info.IsSelected = false;
			foreach (ParentDiffHgFileInfo info in e.AddedItems)
				info.IsSelected = true;

			if (parent_diff != null && list_view != null)
			{
				if (list_view.SelectedItems.Count == 1)
				{
					SelectedParentFile = new SelectedParentFile
					{
						FileInfo = ((ParentDiffHgFileInfo)list_view.SelectedItem).FileInfo,
						ParentFilesDiff = parent_diff
					};

					ShowFileDiff();
				}
				e.Handled = true;
			}
		}

		//-----------------------------------------------------------------------------
		private void tabParentsDiff_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (e.AddedItems.Count == 1)
			{
				SelectedParentFile = null;
				diffColorizer.Clear();

				var parent_diff = GetSelectedParentDiff();
				if (parent_diff == null)
					return;

				var lists = new[] {tabList1, tabList2};
				var list_view = lists.FirstOrDefault(l => l.DataContext == parent_diff);

				if (list_view != null)
				{
					if (list_view.SelectedItems.Count == 1)
					{
						SelectedParentFile = new SelectedParentFile
						{
							FileInfo = ((ParentDiffHgFileInfo)list_view.SelectedItem).FileInfo,
							ParentFilesDiff = parent_diff
						};

						Logger.WriteLine("Show file diff after tab select");
						ShowFileDiff();
					}
					e.Handled = true;
				}
			}
		}

		//-----------------------------------------------------------------------------
		private void listViewFiles_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
		{
			Logger.WriteLine("Visible changed {0}, e {1}", sender, e.NewValue);
			if ((bool)e.NewValue)
			{
				var list_view = sender as ListView;
				if (list_view == null)
					return;

				if (list_view.SelectedItem != null)
					return;

				var parent_diff = list_view.DataContext as ParentFilesDiff;
				if (parent_diff == null)
					return;

				var file = parent_diff.Files.FirstOrDefault(f => f.IsSelected);
				if (file != null)
				{
					Logger.WriteLine("OnVisibleChanged, Scrolling list {0}, {1}", list_view.Items.Count, file.FileInfo.File);
					list_view.ScrollIntoView(file);
				}
			}
		}
	}

	//==================================================================
	class FileHistoryInfo : DependencyObject
	{
		public ChangeDesc ChangeDesc { get; set; }
		public RenameInfo RenameInfo { get; set; }
		public string GroupText { get; set; }

		//-----------------------------------------------------------------------------
		public static readonly System.Windows.DependencyProperty IsCurrentProperty =
			System.Windows.DependencyProperty.Register("IsCurrent", typeof(bool),
			typeof(FileHistoryInfo));

		//-----------------------------------------------------------------------------
		public bool IsCurrent
		{
			get { return (bool)this.GetValue(IsCurrentProperty); }
			set { this.SetValue(IsCurrentProperty, value); }
		}

		//-----------------------------------------------------------------------------
		public static readonly System.Windows.DependencyProperty BranchInfoProperty =
			System.Windows.DependencyProperty.Register("BranchInfo", typeof(BranchInfo),
			typeof(FileHistoryInfo));

		//-----------------------------------------------------------------------------
		internal BranchInfo BranchInfo
		{
			get { return (BranchInfo)this.GetValue(BranchInfoProperty); }
			set { this.SetValue(BranchInfoProperty, value); }
		}

	}

	//==================================================================
	class FileHistoryInfo2 : DependencyObject
	{
		public RevLogChangeDesc ChangeDesc { get; set; }
		public string FileName { get; set; }
		public string GroupText { get; set; }

		//-----------------------------------------------------------------------------
		public static readonly System.Windows.DependencyProperty IsCurrentProperty =
			System.Windows.DependencyProperty.Register("IsCurrent", typeof(bool),
			typeof(FileHistoryInfo2));

		//-----------------------------------------------------------------------------
		public bool IsCurrent
		{
			get { return (bool)this.GetValue(IsCurrentProperty); }
			set { this.SetValue(IsCurrentProperty, value); }
		}

		//-----------------------------------------------------------------------------
		public static readonly System.Windows.DependencyProperty BranchInfoProperty =
			System.Windows.DependencyProperty.Register("BranchInfo", typeof(BranchInfo),
			typeof(FileHistoryInfo2));

		//-----------------------------------------------------------------------------
		internal BranchInfo BranchInfo
		{
			get { return (BranchInfo)this.GetValue(BranchInfoProperty); }
			set { this.SetValue(BranchInfoProperty, value); }
		}

	}

	//-----------------------------------------------------------------------------
	class RenameParts
	{
		public string FileName { get; set; }
		public List<RevLogChangeDesc> Revs { get; set; }
	}
}
