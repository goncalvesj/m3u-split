using System.Collections.Generic;

namespace M3U.Split
{
    public class CategoriesDto
    {
        public string FileName { get; set; }
        public bool GenerateLink { get; set; }
        public List<Category> Categories { get; set; }
    }
}
