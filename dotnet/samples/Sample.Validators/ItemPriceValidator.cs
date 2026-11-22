using HolySheet.Core;

namespace Sample.Validators
{
    public sealed class ItemPriceValidator : ITableValidator
    {
        public void Validate(ValidationContext context)
        {
            foreach (var row in context.Table("Item").Rows)
            {
                int price = row.Get<int>("Price");
                if (price < 0)
                {
                    context.Error(row, "Price", "Price must be zero or greater.");
                }
                else if (price == 0)
                {
                    context.Warning(row, "Price", "This item is free. Check that this is intentional.");
                }
            }
        }
    }
}
