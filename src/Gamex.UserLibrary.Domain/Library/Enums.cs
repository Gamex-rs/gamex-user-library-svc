namespace Gamex.UserLibrary.Domain.Library;

// Serialized as snake_case strings in the database and the API ("like_new", "want_to_buy").

public enum GameFormat
{
    Physical,
    Digital,
}

public enum GameCondition
{
    New,
    LikeNew,
    Good,
    Acceptable,
}

public enum ListingStatus
{
    None,
    Active,
    Completed,
    Expired,
}

public enum ListingType
{
    Exchange,
    Sale,
    WantToBuy,
}
