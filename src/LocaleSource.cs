using System.Collections.Generic;
using Colossal;

namespace DisasterControlPanel
{
    public sealed class LocaleSource : IDictionarySource
    {
        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>();
            string[] ids = { "Earthquake", "Meteor", "Sinkhole", "Evacuation" };
            string[] names = { "地震", "陨石", "地面塌陷", "全城避难" };
            for (int i = 0; i < ids.Length; i++) for (int level = 1; level <= 10; level++)
            {
                string id = DisasterCatalogSystem.Prefix + ids[i] + ".L" + level;
                entries["Assets.NAME[" + id + "]"] = names[i] + "（" + level + "级）";
            }
            return entries;
        }
        public void Unload() { }
    }
}
