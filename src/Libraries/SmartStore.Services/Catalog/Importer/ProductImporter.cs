﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SmartStore.Core.Data;
using SmartStore.Core.Domain.Catalog;
using SmartStore.Core.Domain.DataExchange;
using SmartStore.Core.Domain.Seo;
using SmartStore.Core.Events;
using SmartStore.Services.DataExchange.Import;
using SmartStore.Services.Localization;
using SmartStore.Services.Media;
using SmartStore.Services.Seo;
using SmartStore.Services.Stores;
using SmartStore.Utilities;

namespace SmartStore.Services.Catalog.Importer
{
	public class ProductImporter : IEntityImporter
	{
		private readonly IRepository<ProductPicture> _rsProductPicture;
		private readonly IRepository<ProductManufacturer> _rsProductManufacturer;
		private readonly IRepository<ProductCategory> _rsProductCategory;
		private readonly IRepository<UrlRecord> _rsUrlRecord;
		private readonly IRepository<Product> _rsProduct;
		private readonly ICommonServices _services;
		private readonly ILanguageService _languageService;
		private readonly ILocalizedEntityService _localizedEntityService;
		private readonly IPictureService _pictureService;
		private readonly IManufacturerService _manufacturerService;
		private readonly ICategoryService _categoryService;
		private readonly IProductService _productService;
		private readonly IUrlRecordService _urlRecordService;
		private readonly IStoreMappingService _storeMappingService;
		private readonly SeoSettings _seoSettings;

		public ProductImporter(
			IRepository<ProductPicture> rsProductPicture,
			IRepository<ProductManufacturer> rsProductManufacturer,
			IRepository<ProductCategory> rsProductCategory,
			IRepository<UrlRecord> rsUrlRecord,
			IRepository<Product> rsProduct,
			ICommonServices services,
			ILanguageService languageService,
			ILocalizedEntityService localizedEntityService,
			IPictureService pictureService,
			IManufacturerService manufacturerService,
			ICategoryService categoryService,
			IProductService productService,
			IUrlRecordService urlRecordService,
			IStoreMappingService storeMappingService,
			SeoSettings seoSettings)
		{
			_rsProductPicture = rsProductPicture;
			_rsProductManufacturer = rsProductManufacturer;
			_rsProductCategory = rsProductCategory;
			_rsUrlRecord = rsUrlRecord;
			_rsProduct = rsProduct;
			_services = services;
			_languageService = languageService;
			_localizedEntityService = localizedEntityService;
			_pictureService = pictureService;
			_manufacturerService = manufacturerService;
			_categoryService = categoryService;
			_productService = productService;
			_urlRecordService = urlRecordService;
			_storeMappingService = storeMappingService;
			_seoSettings = seoSettings;
		}

		private int? ZeroToNull(object value, CultureInfo culture)
		{
			int result;
			if (CommonHelper.TryConvert<int>(value, culture, out result) && result > 0)
			{
				return result;
			}

			return (int?)null;
		}

		private void ProcessProductPictures(ImportExecuteContext context, ImportRow<Product>[] batch)
		{
			// true, cause pictures must be saved and assigned an id
			// prior adding a mapping.
			_rsProductPicture.AutoCommitEnabled = true;

			ProductPicture lastInserted = null;
			int equalPictureId = 0;

			foreach (var row in batch)
			{
				var pictures = new string[]
				{
 					row.GetDataValue<string>("Picture1"),
					row.GetDataValue<string>("Picture2"),
					row.GetDataValue<string>("Picture3")
				};

				int i = 0;
				try
				{
					for (i = 0; i < pictures.Length; i++)
					{
						var picture = pictures[i];

						if (picture.IsEmpty() || !File.Exists(picture))
							continue;

						var currentPictures = _rsProductPicture.TableUntracked.Expand(x => x.Picture).Where(x => x.ProductId == row.Entity.Id).Select(x => x.Picture).ToList();
						var pictureBinary = _pictureService.FindEqualPicture(picture, currentPictures, out equalPictureId);

						if (pictureBinary != null && pictureBinary.Length > 0)
						{
							// no equal picture found in sequence
							var newPicture = _pictureService.InsertPicture(pictureBinary, "image/jpeg", _pictureService.GetPictureSeName(row.EntityDisplayName), true, false, false);
							if (newPicture != null)
							{
								var mapping = new ProductPicture
								{
									ProductId = row.Entity.Id,
									PictureId = newPicture.Id,
									DisplayOrder = 1,
								};
								_rsProductPicture.Insert(mapping);
								lastInserted = mapping;
							}
						}
						else
						{
							context.Result.AddInfo("Found equal picture in data store. Skipping field.", row.GetRowInfo(), "Picture" + (i + 1).ToString());
						}
					}
				}
				catch (Exception exception)
				{
					context.Result.AddWarning(exception.Message, row.GetRowInfo(), "Picture" + (i + 1).ToString());
				}

			}

			// Perf: notify only about LAST insertion and update
			if (lastInserted != null)
				_services.EventPublisher.EntityInserted(lastInserted);
		}

		private int ProcessProductManufacturers(ImportExecuteContext context, ImportRow<Product>[] batch)
		{
			_rsProductManufacturer.AutoCommitEnabled = false;

			ProductManufacturer lastInserted = null;

			foreach (var row in batch)
			{
				var manufacturerIds = row.GetDataValue<List<int>>("ManufacturerIds");
				if (manufacturerIds != null && manufacturerIds.Any())
				{
					try
					{
						foreach (var id in manufacturerIds)
						{
							if (_rsProductManufacturer.TableUntracked.Where(x => x.ProductId == row.Entity.Id && x.ManufacturerId == id).FirstOrDefault() == null)
							{
								// ensure that manufacturer exists
								var manufacturer = _manufacturerService.GetManufacturerById(id);
								if (manufacturer != null)
								{
									var productManufacturer = new ProductManufacturer
									{
										ProductId = row.Entity.Id,
										ManufacturerId = manufacturer.Id,
										IsFeaturedProduct = false,
										DisplayOrder = 1
									};
									_rsProductManufacturer.Insert(productManufacturer);
									lastInserted = productManufacturer;
								}
							}
						}
					}
					catch (Exception exception)
					{
						context.Result.AddWarning(exception.Message, row.GetRowInfo(), "ManufacturerIds");
					}
				}
			}

			// commit whole batch at once
			var num = _rsProductManufacturer.Context.SaveChanges();

			// Perf: notify only about LAST insertion and update
			if (lastInserted != null)
				_services.EventPublisher.EntityInserted(lastInserted);

			return num;
		}

		private int ProcessProductCategories(ImportExecuteContext context, ImportRow<Product>[] batch)
		{
			_rsProductCategory.AutoCommitEnabled = false;

			ProductCategory lastInserted = null;

			foreach (var row in batch)
			{
				var categoryIds = row.GetDataValue<List<int>>("CategoryIds");
				if (categoryIds != null && categoryIds.Any())
				{
					try
					{
						foreach (var id in categoryIds)
						{
							if (_rsProductCategory.TableUntracked.Where(x => x.ProductId == row.Entity.Id && x.CategoryId == id).FirstOrDefault() == null)
							{
								// ensure that category exists
								var category = _categoryService.GetCategoryById(id);
								if (category != null)
								{
									var productCategory = new ProductCategory
									{
										ProductId = row.Entity.Id,
										CategoryId = category.Id,
										IsFeaturedProduct = false,
										DisplayOrder = 1
									};
									_rsProductCategory.Insert(productCategory);
									lastInserted = productCategory;
								}
							}
						}
					}
					catch (Exception exception)
					{
						context.Result.AddWarning(exception.Message, row.GetRowInfo(), "CategoryIds");
					}
				}
			}

			// commit whole batch at once
			var num = _rsProductCategory.Context.SaveChanges();

			// Perf: notify only about LAST insertion and update
			if (lastInserted != null)
				_services.EventPublisher.EntityInserted(lastInserted);

			return num;
		}

		private int ProcessLocalizations(ImportExecuteContext context, ImportRow<Product>[] batch)
		{
			//_rsProductManufacturer.AutoCommitEnabled = false;

			//string lastInserted = null;

			var languages = _languageService.GetAllLanguages(true);

			foreach (var row in batch)
			{

				Product product = null;

				//get product
				try
				{
					product = _productService.GetProductById(row.Entity.Id);
				}
				catch (Exception exception)
				{
					context.Result.AddWarning(exception.Message, row.GetRowInfo(), "ProcessLocalizations Product");
				}

				foreach (var lang in languages)
				{
					string localizedName = row.GetDataValue<string>("Name", lang.UniqueSeoCode);
					string localizedShortDescription = row.GetDataValue<string>("ShortDescription", lang.UniqueSeoCode);
					string localizedFullDescription = row.GetDataValue<string>("FullDescription", lang.UniqueSeoCode);

					if (localizedName.HasValue())
					{
						_localizedEntityService.SaveLocalizedValue(product, x => x.Name, localizedName, lang.Id);
					}
					if (localizedShortDescription.HasValue())
					{
						_localizedEntityService.SaveLocalizedValue(product, x => x.ShortDescription, localizedShortDescription, lang.Id);
					}
					if (localizedFullDescription.HasValue())
					{
						_localizedEntityService.SaveLocalizedValue(product, x => x.FullDescription, localizedFullDescription, lang.Id);
					}
				}
			}

			// commit whole batch at once
			var num = _rsProductManufacturer.Context.SaveChanges();

			// Perf: notify only about LAST insertion and update
			//if (lastInserted != null)
			//    _eventPublisher.EntityInserted(lastInserted);

			return num;
		}

		private int ProcessSlugs(ImportExecuteContext context, ImportRow<Product>[] batch)
		{
			var slugMap = new Dictionary<string, UrlRecord>(100);
			Func<string, UrlRecord> slugLookup = ((s) =>
			{
				if (slugMap.ContainsKey(s))
				{
					return slugMap[s];
				}
				return (UrlRecord)null;
			});

			var entityName = typeof(Product).Name;

			foreach (var row in batch)
			{
				if (row.IsNew || row.NameChanged || row.Segmenter.HasDataColumn("SeName"))
				{
					try
					{
						string seName = row.GetDataValue<string>("SeName");
						seName = row.Entity.ValidateSeName(seName, row.Entity.Name, true, _urlRecordService, _seoSettings, extraSlugLookup: slugLookup);

						UrlRecord urlRecord = null;

						if (row.IsNew)
						{
							// dont't bother validating SeName for new entities.
							urlRecord = new UrlRecord
							{
								EntityId = row.Entity.Id,
								EntityName = entityName,
								Slug = seName,
								LanguageId = 0,
								IsActive = true,
							};
							_rsUrlRecord.Insert(urlRecord);
						}
						else
						{
							urlRecord = _urlRecordService.SaveSlug(row.Entity, seName, 0);
						}

						if (urlRecord != null)
						{
							// a new record was inserted to the store: keep track of it for this batch.
							slugMap[seName] = urlRecord;
						}
					}
					catch (Exception exception)
					{
						context.Result.AddWarning(exception.Message, row.GetRowInfo(), "SeName");
					}
				}
			}

			// commit whole batch at once
			return _rsUrlRecord.Context.SaveChanges();
		}

		private int ProcessProducts(ImportExecuteContext context, ImportRow<Product>[] batch)
		{
			_rsProduct.AutoCommitEnabled = true;

			Product lastInserted = null;
			Product lastUpdated = null;

			foreach (var row in batch)
			{
				Product product = null;

				object key;
				var dataRow = row.DataRow;

				// try get by int ID
				if (dataRow.TryGetValue("Id", out key) && key.ToString().ToInt() > 0)
				{
					product = _productService.GetProductById(key.ToString().ToInt());
				}

				// try get by SKU
				if (product == null && dataRow.TryGetValue("SKU", out key))
				{
					product = _productService.GetProductBySku(key.ToString());
				}

				// try get by GTIN
				if (product == null && dataRow.TryGetValue("Gtin", out key))
				{
					product = _productService.GetProductByGtin(key.ToString());
				}

				if (product == null)
				{
					// a Name is required with new products.
					if (!row.Segmenter.HasDataColumn("Name"))
					{
						context.Result.AddError("The 'Name' field is required for new products. Skipping row.", row.GetRowInfo(), "Name");
						continue;
					}
					product = new Product();
				}

				string name = row.GetDataValue<string>("Name");

				row.Initialize(product, name);

				if (!row.IsNew)
				{
					if (!product.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
					{
						// Perf: use this later for SeName updates.
						row.NameChanged = true;
					}
				}

				row.SetProperty(context.Result, product, (x) => x.Sku);
				row.SetProperty(context.Result, product, (x) => x.Gtin);
				row.SetProperty(context.Result, product, (x) => x.ManufacturerPartNumber);
				row.SetProperty(context.Result, product, (x) => x.ProductTypeId, (int)ProductType.SimpleProduct);
				row.SetProperty(context.Result, product, (x) => x.ParentGroupedProductId);
				row.SetProperty(context.Result, product, (x) => x.VisibleIndividually, true);
				row.SetProperty(context.Result, product, (x) => x.Name);
				row.SetProperty(context.Result, product, (x) => x.ShortDescription);
				row.SetProperty(context.Result, product, (x) => x.FullDescription);
				row.SetProperty(context.Result, product, (x) => x.ProductTemplateId);
				row.SetProperty(context.Result, product, (x) => x.ShowOnHomePage);
				row.SetProperty(context.Result, product, (x) => x.HomePageDisplayOrder);
				row.SetProperty(context.Result, product, (x) => x.MetaKeywords);
				row.SetProperty(context.Result, product, (x) => x.MetaDescription);
				row.SetProperty(context.Result, product, (x) => x.MetaTitle);
				row.SetProperty(context.Result, product, (x) => x.AllowCustomerReviews, true);
				row.SetProperty(context.Result, product, (x) => x.Published, true);
				row.SetProperty(context.Result, product, (x) => x.IsGiftCard);
				row.SetProperty(context.Result, product, (x) => x.GiftCardTypeId);
				row.SetProperty(context.Result, product, (x) => x.RequireOtherProducts);
				row.SetProperty(context.Result, product, (x) => x.RequiredProductIds);
				row.SetProperty(context.Result, product, (x) => x.AutomaticallyAddRequiredProducts);
				row.SetProperty(context.Result, product, (x) => x.IsDownload);
				row.SetProperty(context.Result, product, (x) => x.DownloadId);
				row.SetProperty(context.Result, product, (x) => x.UnlimitedDownloads, true);
				row.SetProperty(context.Result, product, (x) => x.MaxNumberOfDownloads, 10);
				row.SetProperty(context.Result, product, (x) => x.DownloadActivationTypeId, 1);
				row.SetProperty(context.Result, product, (x) => x.HasSampleDownload);
				row.SetProperty(context.Result, product, (x) => x.SampleDownloadId, (int?)null, ZeroToNull);
				row.SetProperty(context.Result, product, (x) => x.HasUserAgreement);
				row.SetProperty(context.Result, product, (x) => x.UserAgreementText);
				row.SetProperty(context.Result, product, (x) => x.IsRecurring);
				row.SetProperty(context.Result, product, (x) => x.RecurringCycleLength, 100);
				row.SetProperty(context.Result, product, (x) => x.RecurringCyclePeriodId);
				row.SetProperty(context.Result, product, (x) => x.RecurringTotalCycles, 10);
				row.SetProperty(context.Result, product, (x) => x.IsShipEnabled, true);
				row.SetProperty(context.Result, product, (x) => x.IsFreeShipping);
				row.SetProperty(context.Result, product, (x) => x.AdditionalShippingCharge);
				row.SetProperty(context.Result, product, (x) => x.IsEsd);
				row.SetProperty(context.Result, product, (x) => x.IsTaxExempt);
				row.SetProperty(context.Result, product, (x) => x.TaxCategoryId, 1);
				row.SetProperty(context.Result, product, (x) => x.ManageInventoryMethodId);
				row.SetProperty(context.Result, product, (x) => x.StockQuantity, 10000);
				row.SetProperty(context.Result, product, (x) => x.DisplayStockAvailability);
				row.SetProperty(context.Result, product, (x) => x.DisplayStockQuantity);
				row.SetProperty(context.Result, product, (x) => x.MinStockQuantity);
				row.SetProperty(context.Result, product, (x) => x.LowStockActivityId);
				row.SetProperty(context.Result, product, (x) => x.NotifyAdminForQuantityBelow, 1);
				row.SetProperty(context.Result, product, (x) => x.BackorderModeId);
				row.SetProperty(context.Result, product, (x) => x.AllowBackInStockSubscriptions);
				row.SetProperty(context.Result, product, (x) => x.OrderMinimumQuantity, 1);
				row.SetProperty(context.Result, product, (x) => x.OrderMaximumQuantity, 10000);
				row.SetProperty(context.Result, product, (x) => x.AllowedQuantities);
				row.SetProperty(context.Result, product, (x) => x.DisableBuyButton);
				row.SetProperty(context.Result, product, (x) => x.DisableWishlistButton);
				row.SetProperty(context.Result, product, (x) => x.AvailableForPreOrder);
				row.SetProperty(context.Result, product, (x) => x.CallForPrice);
				row.SetProperty(context.Result, product, (x) => x.Price);
				row.SetProperty(context.Result, product, (x) => x.OldPrice);
				row.SetProperty(context.Result, product, (x) => x.ProductCost);
				row.SetProperty(context.Result, product, (x) => x.SpecialPrice);
				row.SetProperty(context.Result, product, (x) => x.SpecialPriceStartDateTimeUtc);
				row.SetProperty(context.Result, product, (x) => x.SpecialPriceEndDateTimeUtc);
				row.SetProperty(context.Result, product, (x) => x.CustomerEntersPrice);
				row.SetProperty(context.Result, product, (x) => x.MinimumCustomerEnteredPrice);
				row.SetProperty(context.Result, product, (x) => x.MaximumCustomerEnteredPrice, 1000);
				row.SetProperty(context.Result, product, (x) => x.Weight);
				row.SetProperty(context.Result, product, (x) => x.Length);
				row.SetProperty(context.Result, product, (x) => x.Width);
				row.SetProperty(context.Result, product, (x) => x.Height);
				row.SetProperty(context.Result, product, (x) => x.DeliveryTimeId);
				row.SetProperty(context.Result, product, (x) => x.QuantityUnitId);
				row.SetProperty(context.Result, product, (x) => x.BasePriceEnabled);
				row.SetProperty(context.Result, product, (x) => x.BasePriceMeasureUnit);
				row.SetProperty(context.Result, product, (x) => x.BasePriceAmount);
				row.SetProperty(context.Result, product, (x) => x.BasePriceBaseAmount);
				row.SetProperty(context.Result, product, (x) => x.BundlePerItemPricing);
				row.SetProperty(context.Result, product, (x) => x.BundlePerItemShipping);
				row.SetProperty(context.Result, product, (x) => x.BundlePerItemShoppingCart);
				row.SetProperty(context.Result, product, (x) => x.BundleTitleText);
				row.SetProperty(context.Result, product, (x) => x.AvailableStartDateTimeUtc);
				row.SetProperty(context.Result, product, (x) => x.AvailableEndDateTimeUtc);
				row.SetProperty(context.Result, product, (x) => x.LimitedToStores);

				var storeIds = row.GetDataValue<List<int>>("StoreIds");
				if (storeIds != null && storeIds.Any())
				{
					_storeMappingService.SaveStoreMappings(product, storeIds.ToArray());
				}

				row.SetProperty(context.Result, product, (x) => x.CreatedOnUtc, DateTime.UtcNow);

				product.UpdatedOnUtc = DateTime.UtcNow;

				if (row.IsTransient)
				{
					_rsProduct.Insert(product);
					lastInserted = product;
				}
				else
				{
					_rsProduct.Update(product);
					lastUpdated = product;
				}
			}

			// commit whole batch at once
			var num = _rsProduct.Context.SaveChanges();

			// Perf: notify only about LAST insertion and update
			if (lastInserted != null)
				_services.EventPublisher.EntityInserted(lastInserted);
			if (lastUpdated != null)
				_services.EventPublisher.EntityUpdated(lastUpdated);

			return num;
		}

		public void Execute(ImportExecuteContext context)
		{
			using (var scope = new DbContextScope(ctx: _rsProduct.Context, autoDetectChanges: false, proxyCreation: false, validateOnSave: false))
			{
				var segmenter = new ImportDataSegmenter<Product>(context.DataTable);
				segmenter.Culture = CultureInfo.CurrentUICulture;

				context.Result.TotalRecords = segmenter.TotalRows;

				while (context.Abort == DataExchangeAbortion.None && segmenter.ReadNextBatch())
				{
					var batch = segmenter.CurrentBatch;

					// Perf: detach all entities
					_rsProduct.Context.DetachAll(false);

					context.SetProgress(segmenter.CurrentSegmentFirstRowIndex - 1, segmenter.TotalRows);

					// ===========================================================================
					// 1.) Import products
					// ===========================================================================
					try
					{
						ProcessProducts(context, batch);
					}
					catch (Exception exception)
					{
						context.Result.AddError(exception, segmenter.CurrentSegment, "ProcessProducts");
					}

					// reduce batch to saved (valid) products.
					// No need to perform import operations on errored products.
					batch = batch.Where(x => x.Entity != null && !x.IsTransient).ToArray();

					// update result object
					context.Result.NewRecords += batch.Count(x => x.IsNew && !x.IsTransient);
					context.Result.ModifiedRecords += batch.Count(x => !x.IsNew && !x.IsTransient);

					// ===========================================================================
					// 2.) Import SEO Slugs
					// IMPORTANT: Unlike with Products AutoCommitEnabled must be TRUE,
					//            as Slugs are going to be validated against existing ones in DB.
					// ===========================================================================
					if (context.DataTable.HasColumn("SeName") || batch.Any(x => x.IsNew || x.NameChanged))
					{
						try
						{
							_rsProduct.Context.AutoDetectChangesEnabled = true;
							ProcessSlugs(context, batch);
						}
						catch (Exception exception)
						{
							context.Result.AddError(exception, segmenter.CurrentSegment, "ProcessSeoSlugs");
						}
						finally
						{
							_rsProduct.Context.AutoDetectChangesEnabled = false;
						}
					}

					// ===========================================================================
					// 3.) Import Localizations
					// ===========================================================================
					try
					{
						ProcessLocalizations(context, batch);
					}
					catch (Exception exception)
					{
						context.Result.AddError(exception, segmenter.CurrentSegment, "ProcessLocalizations");
					}

					// ===========================================================================
					// 4.) Import product category mappings
					// ===========================================================================
					if (context.DataTable.HasColumn("CategoryIds"))
					{
						try
						{
							ProcessProductCategories(context, batch);
						}
						catch (Exception exception)
						{
							context.Result.AddError(exception, segmenter.CurrentSegment, "ProcessProductCategories");
						}
					}

					// ===========================================================================
					// 5.) Import product manufacturer mappings
					// ===========================================================================
					if (context.DataTable.HasColumn("ManufacturerIds"))
					{
						try
						{
							ProcessProductManufacturers(context, batch);
						}
						catch (Exception exception)
						{
							context.Result.AddError(exception, segmenter.CurrentSegment, "ProcessProductManufacturers");
						}
					}

					// ===========================================================================
					// 6.) Import product picture mappings
					// ===========================================================================
					if (context.DataTable.HasColumn("Picture1") || context.DataTable.HasColumn("Picture2") || context.DataTable.HasColumn("Picture3"))
					{
						try
						{
							ProcessProductPictures(context, batch);
						}
						catch (Exception exception)
						{
							context.Result.AddError(exception, segmenter.CurrentSegment, "ProcessProductPictures");
						}
					}
				}
			}
		}
	}
}
