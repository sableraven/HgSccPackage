// SccProviderStorage.cs : The class implements a fake source control storage for the SccProvider package
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.IO;
using HgSccHelper;

namespace HgSccPackage
{
	// This class defines basic source control status values
	public enum SourceControlStatus
	{
		scsUncontrolled = 0,
		scsCheckedIn,
		scsCheckedOut
	};

	//------------------------------------------------------------------
	public class SourceControlInfo
	{
		public string File { get; set; }
		public SourceControlStatus Status { get; set; }
	};

	//==================================================================
	public class SccProviderStorage
	{
		private HgScc hgscc;
		private Dictionary<string, HgFileInfo> cache;

		//------------------------------------------------------------------
		public SccProviderStorage()
		{
			cache = new Dictionary<string, HgFileInfo>();
		}

		//------------------------------------------------------------------
		public bool IsValid
		{
			get
			{
				return hgscc != null && !String.IsNullOrEmpty(hgscc.WorkingDir); 
			}
		}

		//------------------------------------------------------------------
		public SccErrors Init(string work_dir, SccOpenProjectFlags flags)
		{
			Misc.Log("SccProviderStorage: {0}", work_dir);
			if (hgscc == null)
				hgscc = new HgScc();

			var err = hgscc.OpenProject(work_dir, flags);
			if (err == SccErrors.Ok)
				ReloadCache();

			return err;
		}

		//------------------------------------------------------------------
		public void ReloadCache()
		{
			if (!IsValid)
				return;
			
			ResetCache();
			
			foreach (var pair in hgscc.QueryInfoFullDict())
			{
				var file = pair.Key;
				var status = pair.Value;

				cache[Path.Combine(hgscc.WorkingDir, file).ToLower()] = status;
			}
		}

		//------------------------------------------------------------------
		public void Close()
		{
			if (hgscc != null)
			{
				hgscc.Dispose();
				hgscc = null;
			}
			cache.Clear();
		}

		//------------------------------------------------------------------
		/// <summary>
		/// Adds files to source control by adding them to the list of "controlled" files in the current project
		/// and changing their attributes to reflect the "checked in" status.
		/// </summary>
		public SccErrors AddFilesToStorage(IEnumerable<string> files)
		{
			if (!IsValid)
				return SccErrors.UnknownError;

			var lst = new List<SccAddFile>();
			foreach (var f in files)
			{
				lst.Add(new SccAddFile{ File = f, Flags = SccAddFlags.FileTypeAuto });
				Misc.Log("Adding: {0}", f);
			}

			var err = hgscc.Add(IntPtr.Zero, lst.ToArray(), "Adding files");
			if (err == SccErrors.Ok)
			{
/*
				// �.�. ��� ���������� ������ ����� ���������� ������,
				// �� ���������� ���
				ResetCache();
*/
				UpdateCache(files);
			}

			return err;
		}

		//------------------------------------------------------------------
		/// <summary>
		/// Renames a "controlled" file. If the project file is being renamed, rename the whole storage file
		/// </summary>
		public SccErrors RenameFileInStorage(string strOldName, string strNewName)
		{
			// FIXME: RenameFiles!!
			if (!IsValid)
				return SccErrors.UnknownError;

			Misc.Log("Rename: {0} to {1}", strOldName, strNewName);

			var err = hgscc.Rename(IntPtr.Zero, strOldName, strNewName);
			if (err == SccErrors.Ok)
			{
/*
				// �.�. ��� �������������� ������ ����� ���������� ������,
				// �� ���������� ���
				ResetCache();
*/
				UpdateCache(new[]{strOldName, strNewName});
			}

			return err;
		}

		//------------------------------------------------------------------
		private static SourceControlStatus FromHgStatus(HgFileStatus status)
		{
			switch (status)
			{
				case HgFileStatus.Added:
					return SourceControlStatus.scsCheckedOut;
				case HgFileStatus.Clean:
					return SourceControlStatus.scsCheckedIn;
				case HgFileStatus.Deleted:
					return SourceControlStatus.scsUncontrolled;
				case HgFileStatus.Ignored:
					return SourceControlStatus.scsUncontrolled;
				case HgFileStatus.Modified:
					return SourceControlStatus.scsCheckedOut;
				case HgFileStatus.NotTracked:
					return SourceControlStatus.scsUncontrolled;
				case HgFileStatus.Removed:
					return SourceControlStatus.scsUncontrolled;
				case HgFileStatus.Tracked:
					return SourceControlStatus.scsCheckedIn;
			}

			return SourceControlStatus.scsUncontrolled;
		}

		//------------------------------------------------------------------
		private static HgFileStatus ToHgStatus(SourceControlStatus status)
		{
			switch (status)
			{
				case SourceControlStatus.scsCheckedIn:
					return HgFileStatus.Tracked;
				case SourceControlStatus.scsCheckedOut:
					return HgFileStatus.Modified;
				case SourceControlStatus.scsUncontrolled:
					return HgFileStatus.NotTracked;
			}

			return HgFileStatus.NotTracked;
		}

		//------------------------------------------------------------------
		public SccErrors GetStatusForFiles(SourceControlInfo[] files)
		{
			if (!IsValid)
				return SccErrors.UnknownError;

			var not_in_cache = new List<string>();

			foreach (var file in files)
			{
				if (!cache.ContainsKey(file.File.ToLower()))
					not_in_cache.Add(file.File);
			}

			if (not_in_cache.Count != 0)
			{
				UpdateCache(not_in_cache);
			}

			foreach (var file in files)
			{
				HgFileInfo info;
				if (cache.TryGetValue(file.File.ToLower(), out info))
				{
					file.Status = FromHgStatus(info.Status);
				}
				Misc.Log("GetFileStatus: {0} = {1}", file.File, file.Status);
			}

			return SccErrors.Ok;
		}

		//------------------------------------------------------------------
		public SccErrors GetStatusForFiles(string[] files, SourceControlStatus[] statuses)
		{
			if (!IsValid)
				return SccErrors.UnknownError;

			var not_in_cache = new List<string>();

			foreach (var file in files)
			{
				if (!cache.ContainsKey(file.ToLower()))
					not_in_cache.Add(file);
			}

			if (not_in_cache.Count != 0)
			{
				UpdateCache(not_in_cache);
			}

			for (int i = 0; i < files.Length; ++i)
			{
				HgFileInfo info;
				if (cache.TryGetValue(files[i].ToLower(), out info))
				{
					statuses[i] = FromHgStatus(info.Status);
				}

				Misc.Log("GetFileStatus: {0} = {1}", files[i], statuses[i]);
			}

			return SccErrors.Ok;
		}

		//------------------------------------------------------------------
		public SccErrors Commit(IEnumerable<string> files, out IEnumerable<string> commited_files)
		{
			if (!IsValid)
			{
				commited_files = new List<string>();
				return SccErrors.UnknownError;
			}

			foreach (var f in files)
			{
				Misc.Log("Commit: {0}", f);
			}


			var error = hgscc.Commit(IntPtr.Zero, files, "", out commited_files);
			if (error == SccErrors.Ok)
			{
				UpdateCache(commited_files);
			}
			return error;
		}

		//------------------------------------------------------------------
		public SccErrors Revert(IEnumerable<string> files, out IEnumerable<string> reverted_files)
		{
			if (!IsValid)
			{
				reverted_files = new List<string>();
				return SccErrors.UnknownError;
			}

			foreach (var f in files)
			{
				Misc.Log("Revert: {0}", f);
			}


			var error = hgscc.Revert(IntPtr.Zero, files, out reverted_files);
			if (error == SccErrors.Ok)
			{
//				UpdateCache(reverted_files);
				ReloadCache();
			}
			return error;
		}

		//------------------------------------------------------------------
		public SccErrors RemoveFiles(IEnumerable<string> files)
		{
			if (!IsValid)
				return SccErrors.UnknownError;

			var error = hgscc.Remove(IntPtr.Zero, files, "");
			if (error != SccErrors.Ok)
				return error;

			foreach (var f in files)
			{
				Misc.Log("Remove: {0}", f);
				SetCacheStatus(f, SourceControlStatus.scsCheckedOut);
			}
			return SccErrors.Ok;
		}

		//------------------------------------------------------------------
		/// <summary>
		/// Returns a source control status inferred from the file's attributes on local disk
		/// </summary>
		public SourceControlStatus GetFileStatus(string filename)
		{
			var info = new SourceControlInfo { File = filename };
			var lst = new SourceControlInfo[]{ info };

			GetStatusForFiles(lst);

			return lst[0].Status;
		}

		//------------------------------------------------------------------
		public void ViewHistory(string filename)
		{
			if (!IsValid)
				return;

			hgscc.History(IntPtr.Zero, filename);
		}

		//------------------------------------------------------------------
		public void RemoveFile(string filename)
		{
			var files = new[] {filename};
			RemoveFiles(files);
		}

		//------------------------------------------------------------------
		private void UpdateCache(IEnumerable<string> files)
		{
			if (!IsValid)
				return;

			var lst = new List<HgFileInfo>();
			foreach (var f in files)
			{
				var info = new HgFileInfo {File = f, Status = HgFileStatus.NotTracked};
				lst.Add(info);
				
				Misc.Log("UpdateCache: {0}", f);
			}
			
			var info_lst = lst.ToArray();

			SccErrors error = hgscc.QueryInfo2(info_lst);
			if (error == SccErrors.Ok)
			{
				foreach (var info in info_lst)
				{
					cache[info.File.ToLower()] = info;
				}
			}
		}

		//------------------------------------------------------------------
		private void ResetCache()
		{
			cache.Clear();
		}

		//------------------------------------------------------------------
		public void UpdateFileCache(string file)
		{
			UpdateCache(new[] {file});
		}

		//------------------------------------------------------------------
		public void SetCacheStatus(string file, SourceControlStatus status)
		{
			Misc.Log("SetCacheStatus: {0}, {1}", file, status);
			
			HgFileInfo info;
			if (cache.TryGetValue(file.ToLower(), out info))
			{
				info.Status = ToHgStatus(status);
			}
			else
			{
				Misc.Log("File not found in cache");
			}
		}

		//------------------------------------------------------------------
		public void Compare(string file)
		{
			if (!IsValid)
				return;

			hgscc.Diff(IntPtr.Zero, file, SccDiffFlags.None);
		}
	}
}
