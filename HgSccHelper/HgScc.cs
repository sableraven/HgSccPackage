﻿using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Windows.Forms;

namespace HgSccHelper
{
	//-----------------------------------------------------------------------------
	class FackedCheckoutInfo
	{
		public string File { get; set; }
	}

	//-----------------------------------------------------------------------------
	public class HgScc : IDisposable
	{
		public string WorkingDir { get; private set; }
		private readonly Hg hg;

		private readonly Queue<HgFileInfo> event_queue;
		private readonly Dictionary<string, HgFileStatus> track_ide_changes;

		//-----------------------------------------------------------------------------
		public HgScc()
		{
			hg = new Hg();
			event_queue = new Queue<HgFileInfo>();
			track_ide_changes = new Dictionary<string, HgFileStatus>();
		}

		//-----------------------------------------------------------------------------
		public void Dispose()
		{
			hg.Dispose();
		}

		//-----------------------------------------------------------------------------
		public bool GetRelativePath(string path, out string relative)
		{
			relative = string.Empty;
			string f = path.ToLower();
			if (!f.StartsWith(WorkingDir))
				return false;

			if (path.Length == WorkingDir.Length)
				relative = "";
			else
				relative = path.Substring(WorkingDir.Length + 1);
			return true;
		}

		//-----------------------------------------------------------------------------
		public SccErrors OpenProject(string local_proj_path, SccOpenProjectFlags flags)
		{
			string root = hg.Root(local_proj_path);
			bool is_root_exist = root.Length > 0;

			if (!is_root_exist)
			{
				if ((flags | SccOpenProjectFlags.CreateIfNew) == SccOpenProjectFlags.CreateIfNew)
				{
					if (!hg.CreateRepository(local_proj_path))
						return SccErrors.CouldNotCreateProject;

					root = local_proj_path;
				}
				else
				{
					if ((flags | SccOpenProjectFlags.SilentOpen) == SccOpenProjectFlags.SilentOpen)
						return SccErrors.UnknownProject;

					return SccErrors.NonSpecificError;
				}
			}

			WorkingDir = root.ToLower();

			return SccErrors.Ok;
		}

		//-----------------------------------------------------------------------------
		private void FixFakedCheckout()
		{
			return;

			var dict = new Dictionary<string, HgFileStatus>();
			var hg_files = hg.Manifest(WorkingDir);

			foreach (var file_status in hg.Status(WorkingDir))
			{
				string file = file_status.File;
				dict.Add(file, file_status.Status);
			}

			foreach (var file in hg_files)
			{
				string f = file.Replace('/', '\\');
				if (!dict.ContainsKey(f))
				{
//					HgFileStatus status = HgFileStatus.Tracked;

					// Checking read-only flag to figure if is was checked out
					var file_path = Path.Combine(WorkingDir, f);
//					Logger.WriteLine(String.Format("WorkDir: {0}, File: {1}, PathComb: {2}", WorkingDir, f, file_path));

					FileAttributes attr = File.GetAttributes(file_path);
					if ((attr & FileAttributes.ReadOnly) != FileAttributes.ReadOnly)
					{
//						status = HgFileStatus.Modified;
						File.SetAttributes(file_path, attr | FileAttributes.ReadOnly);
						Logger.WriteLine(String.Format("FixFakedCheckout: {0}", file_path));
					}
				}
			}

/*
			foreach (var file_status in hg.Status(WorkingDir))
			{
				if (file_status.Status == HgFileStatus.Tracked)
				{
					var file_path = Path.Combine(WorkingDir, file_status.File);
					FileAttributes attr = File.GetAttributes(file_path);
					if ((attr & FileAttributes.ReadOnly) != FileAttributes.ReadOnly)
					{
						File.SetAttributes(file_path, attr | FileAttributes.ReadOnly);

						Logger.WriteLine(String.Format("FixFakedCheckout: {0}", file_path));
					}
				}
			}
*/
		}

		//-----------------------------------------------------------------------------
		public SccErrors CloseProject()
		{
			return SccErrors.Ok;
		}

		//-----------------------------------------------------------------------------
		private static SccStatus ToSccStatus(HgFileStatus status)
		{
			SccStatus scc = SccStatus.None;
			switch (status)
			{
				case HgFileStatus.Added: scc |= SccStatus.Controlled | SccStatus.CheckedOut | SccStatus.OutByUser | SccStatus.Modified; break;
				case HgFileStatus.Clean: scc |= SccStatus.Controlled; break;
				case HgFileStatus.Deleted: scc |= SccStatus.Controlled | SccStatus.Deleted; break;
				case HgFileStatus.Ignored: scc |= SccStatus.NotControlled; break;
				case HgFileStatus.Modified: scc |= SccStatus.Controlled | SccStatus.CheckedOut | SccStatus.OutByUser | SccStatus.Modified; break;
				case HgFileStatus.NotTracked: scc |= SccStatus.NotControlled; break;
				case HgFileStatus.Removed: scc |= SccStatus.Controlled | SccStatus.Deleted; break;
				case HgFileStatus.Tracked: scc |= SccStatus.Controlled; break;
				default:
					scc = SccStatus.Invalid;
					break;
			}

			return scc;
		}

		//-----------------------------------------------------------------------------
		public SccErrors LookupProjects(string folder, out SccFileInfo[] files)
		{
			var dict = new Dictionary<string, bool>();
			dict.Add(".sln", true);
			dict.Add(".atp", true);
			dict.Add(".dbp", true);
			dict.Add(".vap", true);
			dict.Add(".vsproj", true);
			dict.Add(".vbdproj", true);
			dict.Add(".vddproj", true);
			dict.Add(".dbproj", true);
			dict.Add(".vbp;*.vip;*.vbproj", true);
			dict.Add(".vcproj", true);
			dict.Add(".vdproj", true);
			dict.Add(".vmx", true);
			dict.Add(".vup", true);
			dict.Add(".csproj", true);

			string[] all_files = Directory.GetFiles(folder);
			var found_list = new List<string>();
			foreach (var f in all_files)
			{
				string ext = Path.GetExtension(f).ToLower();
				if (dict.ContainsKey(ext))
					found_list.Add(f);
			}

			string[] found_files = found_list.ToArray();
			files = new SccFileInfo[found_files.Length];
			
			for (int i = 0; i < found_files.Length; ++i)
			{
				files[i] = new SccFileInfo { File = Path.GetFullPath(found_files[i]), Status = SccStatus.None };
				Logger.WriteLine("-> " + files[i].File);
			}

			return QueryInfo(files);
		}

		//-----------------------------------------------------------------------------
		private Dictionary<string, HgFileInfo> QueryInfoFullDict()
		{
			var dict = new Dictionary<string, HgFileInfo>();
//			var hg_files = hg.Manifest(WorkingDir);

			foreach (var file_status in hg.Status(WorkingDir))
			{
				dict.Add(file_status.File, file_status);
			}

/*
			foreach (var file in hg_files)
			{
				string f = file.Replace('/', '\\');
				if (!dict.ContainsKey(f))
				{
//					Logger.WriteLine(String.Format("Manifest File: {0}", f));
					HgFileStatus status = HgFileStatus.Tracked;
					dict.Add(f, new HgFileInfo { File = f, Status = status } );
				}
			}
*/

			return dict;
		}

		//-----------------------------------------------------------------------------
		internal SccErrors QueryInfo(HgFileInfo[] files)
		{
			// TODO: Check if project is opened
//			var hg_files = hg.Manifest(WorkingDir);
			var dict = new Dictionary<string, HgFileStatus>();

			foreach (var file_status in hg.Status(WorkingDir))
			{
				string file = file_status.File.ToLower();
				dict.Add(file, file_status.Status);
			}

/*
			foreach (var file in hg_files)
			{
				string f = file.ToLower().Replace('/', '\\');
				if (!dict.ContainsKey(f))
				{
					//					Logger.WriteLine(String.Format("Manifest File: {0}", f));
					dict.Add(f, HgFileStatus.Tracked);
				}
			}
*/

			var queue_dict = new Dictionary<string, HgFileStatus>();
			while (event_queue.Count > 0)
			{
				var item = event_queue.Dequeue();
				queue_dict.Add(item.File, item.Status);
			}

			foreach (var info in files)
			{
				HgFileStatus status = HgFileStatus.NotTracked;
				string file;
				if (GetRelativePath(info.File, out file)
					&& dict.TryGetValue(file.ToLower(), out status))
				{
					info.Status = status;
				}
				else
				{
					info.Status = HgFileStatus.NotTracked;
				}

				if (dict.ContainsKey(file))
					dict.Remove(file);
			}

			foreach (var tuple in queue_dict)
			{
				event_queue.Enqueue(new HgFileInfo { File = tuple.Key, Status = tuple.Value });
			}

			return SccErrors.Ok;
		}


		//-----------------------------------------------------------------------------
		public SccErrors QueryInfo(SccFileInfo [] files)
		{
			var lst = new HgFileInfo[files.Length];
			for (int i = 0; i < files.Length; ++i)
				lst[i] = new HgFileInfo { File = files[i].File };

			SccErrors error = QueryInfo(lst);

			for (int i = 0; i < files.Length; ++i)
				files[i].Status = ToSccStatus(lst[i].Status);

			return error;
		}

		//-----------------------------------------------------------------------------
		public SccErrors Add(IntPtr hwnd, SccAddFile[] files, string comment)
		{
			var lst = new List<string>();
			foreach (var f in files)
				lst.Add(f.File);

			return Add(hwnd, lst.ToArray());
		}

		//-----------------------------------------------------------------------------
		public SccErrors Add(IntPtr hwnd, string[] files)
		{
			// TODO: Check if project is opened
			var add_files = new List<string>();
			int count = 0;

			for (int i = 0; i < files.Length; ++i)
			{
				string f;
				if (!GetRelativePath(files[i], out f))
					return SccErrors.InvalidFilePath;

				add_files.Add(f);
				count += f.Length;

				if (count > 30000)
				{
					if (!hg.Add(WorkingDir, add_files.ToArray()))
						return SccErrors.OpNotPerformed;

					foreach (var add_f in add_files)
					{
						string lower_f = add_f.ToLower();
						if (!track_ide_changes.ContainsKey(lower_f))
							track_ide_changes.Add(lower_f, HgFileStatus.Added);
					}

					add_files.Clear();
					count = 0;
				}
			}

			if (add_files.Count > 0)
			{
				foreach (var add_f in add_files)
				{
					string lower_f = add_f.ToLower();
					if (!track_ide_changes.ContainsKey(lower_f))
						track_ide_changes.Add(lower_f, HgFileStatus.Added);
				}

				if (!hg.Add(WorkingDir, add_files.ToArray()))
					return SccErrors.OpNotPerformed;
			}
			return SccErrors.Ok;
		}
		
		//-----------------------------------------------------------------------------
		private SccErrors CommitInternal(IntPtr hwnd, string[] files, string comment)
		{
			var dict = new Dictionary<string, HgFileStatus>();
			var files_status_dict = QueryInfoFullDict();

			foreach (var f in files)
			{
				string file;
				if (!GetRelativePath(f, out file))
					return SccErrors.NonSpecificError;

				dict.Add(file.ToLower(), HgFileStatus.NotTracked);
			}

			using (var form = new CommitForm())
			{
				form.Comment = comment;
				form.Hg = hg;
				form.WorkingDir = WorkingDir;

				var commit_files = new List<CommitListItem>();
				var commit_removed = new Dictionary<string, CommitListItem>();

//				foreach (var f in files_status)
				foreach (var tuple in files_status_dict)
				{
					var f = tuple.Value;

					switch (f.Status)
					{
						case HgFileStatus.Added:
						//						case HgFileStatus.Deleted:
						case HgFileStatus.Modified:
						case HgFileStatus.Removed:
							{
								var item = new CommitListItem();
								string lower_f = f.File.ToLower();
								item.Checked = dict.ContainsKey(lower_f) || track_ide_changes.ContainsKey(lower_f);
								item.FileInfo = f;

								commit_files.Add(item);
								if (f.Status == HgFileStatus.Removed)
									commit_removed.Add(f.File, item);
								break;
							}
					}
				}

				foreach (var f in commit_files)
				{
					if (	f.Checked
						&&	f.FileInfo.Status == HgFileStatus.Added
						&&	!String.IsNullOrEmpty(f.FileInfo.CopiedFrom))
					{
						CommitListItem item;
						
						if (commit_removed.TryGetValue(f.FileInfo.CopiedFrom, out item))
						{
							Logger.WriteLine("commit_removed: " + item.FileInfo.File);
							item.Checked = true;
						}
					}
				}

				if (commit_files.Count == 0)
					return SccErrors.Ok;

				form.SetItems(commit_files);

				if (form.ShowDialog() == DialogResult.OK)
				{
					var checked_files = form.GetCheckedItems();
					if (checked_files.Count == 0)
						return SccErrors.Ok;

					string[] to_commit_files = new string[checked_files.Count];
					for (int i = 0; i < checked_files.Count; ++i)
					{
						to_commit_files[i] = checked_files[i].FileInfo.File;
						if (checked_files[i].FileInfo.Status == HgFileStatus.Added)
						{
							event_queue.Enqueue(new HgFileInfo { File = to_commit_files[i], Status = HgFileStatus.Tracked });
						}
					}

					SccErrors error = CheckInInternal(hwnd, to_commit_files, form.Comment);
					if (error == SccErrors.Ok)
					{
						foreach (var f in to_commit_files)
						{
							string lower_f = f.ToLower();
							track_ide_changes.Remove(lower_f);
						}
					}

					return error;
				}
				else
				{
					return SccErrors.I_OperationCanceled;
				}
			}
		}

		//-----------------------------------------------------------------------------
		private SccErrors CommitInternalOld(IntPtr hwnd, string[] files, string comment)
		{
			var dict = new Dictionary<string, HgFileStatus>();
			var files_status = hg.Status(WorkingDir);

			foreach (var f in files)
			{
				string file;
				if (!GetRelativePath(f, out file))
					return SccErrors.NonSpecificError;

				dict.Add(file.ToLower(), HgFileStatus.NotTracked);
			}

			using (var form = new CommitForm())
			{
				form.Comment = comment;
				form.Hg = hg;
				form.WorkingDir = WorkingDir;

				var commit_files = new List<CommitListItem>();

				foreach (var f in files_status)
				{
					switch (f.Status)
					{
						case HgFileStatus.Added:
//						case HgFileStatus.Deleted:
						case HgFileStatus.Modified:
						case HgFileStatus.Removed:
							{
								var item = new CommitListItem();
								item.Checked = dict.ContainsKey(f.File.ToLower());
								item.FileInfo = f;

								commit_files.Add(item);
								break;
							}
					}
				}

				if (commit_files.Count == 0)
					return SccErrors.Ok;

				form.SetItems(commit_files);

				if (form.ShowDialog() == DialogResult.OK)
				{
					var checked_files = form.GetCheckedItems();
					if (checked_files.Count == 0)
						return SccErrors.Ok;

					string[] commint_files = new string[checked_files.Count];
					for(int i = 0; i < checked_files.Count; ++i)
						commint_files[i] = checked_files[i].FileInfo.File;

					return CheckInInternal(hwnd, commint_files, form.Comment);
				}
				else
				{
					return SccErrors.I_OperationCanceled;
				}
			}
		}

		//-----------------------------------------------------------------------------
		public SccErrors CheckInInternal(IntPtr hwnd, string[] files, string comment)
		{
			// TODO: Check if project is opened
			var checkin_files = new List<string>();
			int count = 0;

			for (int i = 0; i < files.Length; ++i)
			{
				string f = files[i];

				checkin_files.Add(f);
				count += f.Length;

				if (count > 30000)
				{
					if (!hg.Commit(WorkingDir, checkin_files.ToArray(), comment))
						return SccErrors.OpNotPerformed;

					checkin_files.Clear();
					count = 0;
				}
			}

			if (checkin_files.Count > 0)
			{
				if (!hg.Commit(WorkingDir, checkin_files.ToArray(), comment))
					return SccErrors.OpNotPerformed;
			}

			return SccErrors.Ok;
		}

		//-----------------------------------------------------------------------------
		public SccErrors CheckIn(IntPtr hwnd, string[] files, string comment)
		{
			return CommitInternal(hwnd, files, comment);
		}

		//-----------------------------------------------------------------------------
		public SccErrors Checkout(IntPtr hwnd, string[] files, string comment)
		{
			var local_files = new List<string>();
			foreach (var f in files)
			{
				string local_f;
				if (!GetRelativePath(f, out local_f))
					return SccErrors.InvalidFilePath;

				local_files.Add(local_f);
			}

			if (!hg.Checkout(WorkingDir, local_files.ToArray()))
				return SccErrors.NonSpecificError;

			return SccErrors.Ok;
		}

		//-----------------------------------------------------------------------------
		public SccErrors Revert(IntPtr hwnd, string[] files)
		{
			var local_files = new List<string>();
			foreach (var f in files)
			{
				string local_f;
				if (!GetRelativePath(f, out local_f))
					return SccErrors.InvalidFilePath;

				local_files.Add(local_f);
			}

			if (!hg.Revert(WorkingDir, local_files.ToArray()))
				return SccErrors.NonSpecificError;

			foreach (var f in local_files)
			{
				track_ide_changes.Remove(f.ToLower());
			}

			return SccErrors.Ok;
		}

		//-----------------------------------------------------------------------------
		public SccErrors Diff(IntPtr hwnd, string filename, SccDiffFlags flags)
		{
			string local_f;
			if (!GetRelativePath(filename, out local_f))
				return SccErrors.InvalidFilePath;

			switch(flags)
			{
				case SccDiffFlags.QdCheckSum:
				case SccDiffFlags.QdContents:
				case SccDiffFlags.QdTime:
				case SccDiffFlags.QuickDiff:
					{
						bool is_different;
						if (!hg.DiffSilent(WorkingDir, local_f, out is_different))
							return SccErrors.NonSpecificError;

						if (is_different)
							return SccErrors.I_FileDiffers;

						return SccErrors.Ok;
					}
				default:
					{
						bool is_different;

						try
						{
							if (!hg.Diff(WorkingDir, local_f, out is_different))
								return SccErrors.NonSpecificError;
						}
						catch (HgDiffException)
						{
							Util.HandleHgDiffException();
							return SccErrors.I_OperationCanceled;
						}

						if (!is_different)
						{
							System.Windows.Forms.MessageBox.Show("File: " + filename + " is up to date", "Diff");
							return SccErrors.Ok;
						}

						return SccErrors.Ok;
					}
			}
		}

		//-----------------------------------------------------------------------------
		public SccErrors Remove(IntPtr hwnd, string[] files, string comment)
		{
			// TODO: Check if project is opened
			var remove_files = new List<string>();
			int count = 0;

			for (int i = 0; i < files.Length; ++i)
			{
				string f;
				if (!GetRelativePath(files[i], out f))
					return SccErrors.InvalidFilePath;

				remove_files.Add(f);
				count += f.Length;

				if (count > 30000)
				{
					if (!hg.Remove(WorkingDir, remove_files.ToArray(), comment))
						return SccErrors.OpNotPerformed;

					foreach (var remove_f in remove_files)
					{
						string lower_f = remove_f.ToLower();
						if (!track_ide_changes.ContainsKey(lower_f))
							track_ide_changes.Add(lower_f, HgFileStatus.Removed);
					}

					remove_files.Clear();
					count = 0;
				}
			}

			if (remove_files.Count > 0)
			{
				if (!hg.Remove(WorkingDir, remove_files.ToArray(), comment))
					return SccErrors.OpNotPerformed;

				foreach (var remove_f in remove_files)
				{
					string lower_f = remove_f.ToLower();
					if (!track_ide_changes.ContainsKey(lower_f))
						track_ide_changes.Add(lower_f, HgFileStatus.Removed);
				}
			}

			return SccErrors.Ok;
		}

		//-----------------------------------------------------------------------------
		public SccErrors Rename(IntPtr hwnd, string file, string new_file)
		{
			string f;
			string new_f;

			if (	!GetRelativePath(file, out f)
				||	!GetRelativePath(new_file, out new_f))
			{
				Logger.WriteLine("Can't get relative path: " + file + ", " + new_file);
				return SccErrors.InvalidFilePath;
			}

			if (!hg.Rename(WorkingDir, f, new_f))
				return SccErrors.NonSpecificError;

			string lower_f = f.ToLower();
			string lower_new_f = new_f.ToLower();
			
			if (!track_ide_changes.ContainsKey(lower_f))
				track_ide_changes.Add(lower_f, HgFileStatus.Removed);

			if (!track_ide_changes.ContainsKey(lower_new_f))
				track_ide_changes.Add(lower_new_f, HgFileStatus.Added);

			return SccErrors.Ok;
		}

		//-----------------------------------------------------------------------------
		public SccErrors GetExtendedCapabilities(SccExCaps cap, out bool is_supported)
		{
			switch(cap)
			{
				case SccExCaps.DeleteCheckedOut:
				case SccExCaps.RenameCheckedOut:
					{
						is_supported = true;
						break;
					}
				default:
					{
						is_supported = false;
						break;
					}
			}

			return SccErrors.Ok;
		}

		//-----------------------------------------------------------------------------
		public SccErrors History(IntPtr hwnd, string file)
		{
			using (var form = new HistoryForm())
			{
				string relative_path;
				if (!GetRelativePath(file, out relative_path))
				{
					Logger.WriteLine("Can't get relative path: " + file);
					return SccErrors.InvalidFilePath;
				}

				var files = hg.Status(WorkingDir, relative_path);
				if (files.Count == 1
					&& files[0].Status == HgFileStatus.Added
					&& files[0].CopiedFrom != null)
				{
					var file_info = files[0];
					relative_path = file_info.CopiedFrom;
				}

				form.Path = relative_path;
	
				form.WorkingDir = WorkingDir;
				form.Hg = hg;

				var changes = hg.ChangesFull(WorkingDir, relative_path);
				if (changes.Count == 0)
				{
					Logger.WriteLine("Changes == 0");
					return SccErrors.I_OperationCanceled;
				}

				form.SetChangesList(changes);
				form.ShowDialog();
			}

			return SccErrors.Ok;
		}

		//-----------------------------------------------------------------------------
		public SccErrors GetEvents(out string filename, out SccStatus status, out int events_remaining)
		{
			if (event_queue.Count == 0)
			{
				filename = string.Empty;
				status = SccStatus.Invalid;
				events_remaining = 0;

				return SccErrors.Ok;
			}

			var item = event_queue.Dequeue();

			filename = Path.Combine(WorkingDir, item.File);
			status = ToSccStatus(item.Status);
			events_remaining = event_queue.Count;
			
			return SccErrors.Ok;
		}

		//-----------------------------------------------------------------------------
		public SccErrors GetCommandOptions(IntPtr hwnd, SccCommand command, ref IntPtr context)
		{
			switch(command)
			{
				case SccCommand.Options:
					{
						if (context == IntPtr.Zero)
						{
							return SccErrors.I_AdvSupport;
						}
						else
						{
							using (var f = new OptionsForm())
							{
								var res = f.ShowDialog();
								if (res == DialogResult.OK)
								{
//									MessageBox.Show(HgSccOptions.Options.DiffTool);
									return SccErrors.Ok;
								}

								return SccErrors.I_OperationCanceled;
							}
						}
					}
			}

			return SccErrors.Ok;
		}

	}
}
