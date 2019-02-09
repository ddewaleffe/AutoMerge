using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AutoMerge
{
	public class ChangesetViewModel
	{
		public int ChangesetId { get; set; }

	    public string Username { get; set; }
        public string UserDisplayName { get; set; }
        public string UserNoDomain => Regex.Replace(Username, "^.+\\\\(.+)$", "$1");
        public string ChangeDate { get; set; }
	    public string Comment { get; set; }
        public string Tooltip => $"By {UserDisplayName} [{Username}] on {ChangeDate}\n{Comment}";
         public List<string> Branches { get; set; }

		public string DisplayBranchName
		{
			get { return BranchHelper.GetDisplayBranchName(Branches); }
		}
	}
}
