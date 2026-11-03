using System;
using System.Collections.Generic;
using System.Linq;
using DicomRtNifti.App.ViewModels;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Xunit;

namespace DicomRtNifti.App.Tests
{
    /// <summary>
    /// What a change to the curated lists does to a user who already chose tags. The picker is
    /// seeded only from the keyword lists settings.json holds, and Convert Selected writes those
    /// lists as they are, so a newly curated keyword is offered in the picker and never chosen
    /// for the user. Pure: no window opens.
    /// </summary>
    public class MetadataTagSelectionTests
    {
        /// <summary>The study keywords the image list gained after users had saved selections.</summary>
        private static readonly string[] NewlyCurated = { "StudyTime", "StudyID", "AccessionNumber", "ReferringPhysicianName" };

        [Fact]
        public void Newly_curated_keywords_are_offered_but_not_selected_for_a_user_with_saved_choices()
        {
            // A user who ticked "Select all (filtered)" over the curated image list before the
            // study keywords joined it saved that list without them.
            var saved = MetadataTagCatalog.ImageComputedOptions.Select(o => o.Key)
                .Concat(MetadataTagCatalog.CuratedImageKeywords.Except(NewlyCurated))
                .ToList();
            var images = new MetadataTagSelectionViewModel(saved, new List<string>(), new List<string>()).ImagesSection;

            foreach (string keyword in NewlyCurated)
            {
                var item = Assert.Single(images.FilteredItems, i => i.Keyword == keyword);
                Assert.True(item.IsCurated, $"{keyword} is not in the curated view");
                Assert.False(item.IsSelected, $"{keyword} was selected without the user choosing it");
            }

            // The header box no longer claims everything is selected, and confirming without a
            // change saves back exactly what was saved before.
            Assert.False(images.AllSelected);
            Assert.Equal(saved.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), images.GetSelectedKeywords());
        }

        [Fact]
        public void A_user_with_no_saved_choices_has_no_tags_selected()
        {
            var settings = new AppSettings();
            Assert.Empty(settings.MetadataImageTagKeywords);
            Assert.Empty(settings.MetadataStructureTagKeywords);
            Assert.Empty(settings.MetadataDoseTagKeywords);

            var picker = new MetadataTagSelectionViewModel(
                settings.MetadataImageTagKeywords, settings.MetadataStructureTagKeywords, settings.MetadataDoseTagKeywords);
            Assert.Empty(picker.ImagesSection.GetSelectedKeywords());
            Assert.Empty(picker.StructuresSection.GetSelectedKeywords());
            Assert.Empty(picker.DoseSection.GetSelectedKeywords());
        }
    }
}
