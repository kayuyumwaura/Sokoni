-- Sokoni schema. Idempotent: safe to run on every startup.
-- Batches are separated by GO lines (also runs as-is in SSMS / Azure Data Studio).

IF OBJECT_ID('dbo.Products') IS NULL
CREATE TABLE dbo.Products (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    Name        NVARCHAR(200)  NOT NULL,
    Description NVARCHAR(1000) NULL,
    Category    NVARCHAR(100)  NULL,
    Price       DECIMAL(12,2)  NOT NULL,
    Stock       INT            NOT NULL CONSTRAINT DF_Products_Stock DEFAULT 0,
    ImageUrl    NVARCHAR(500)  NULL,
    Active      BIT            NOT NULL CONSTRAINT DF_Products_Active DEFAULT 1,
    CreatedAt   DATETIME2      NOT NULL CONSTRAINT DF_Products_CreatedAt DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID('dbo.Conversations') IS NULL
CREATE TABLE dbo.Conversations (
    Id            INT IDENTITY(1,1) PRIMARY KEY,
    Channel       TINYINT       NOT NULL,
    CustomerId    NVARCHAR(100) NOT NULL,
    CustomerName  NVARCHAR(200) NULL,
    Phone         NVARCHAR(20)  NULL,
    HumanTakeover BIT           NOT NULL CONSTRAINT DF_Conversations_Takeover DEFAULT 0,
    CreatedAt     DATETIME2     NOT NULL CONSTRAINT DF_Conversations_CreatedAt DEFAULT SYSUTCDATETIME(),
    LastInboundAt DATETIME2     NULL,
    CONSTRAINT UQ_Conversations_Channel_Customer UNIQUE (Channel, CustomerId)
);
GO

IF OBJECT_ID('dbo.Messages') IS NULL
CREATE TABLE dbo.Messages (
    Id             BIGINT IDENTITY(1,1) PRIMARY KEY,
    ConversationId INT           NOT NULL CONSTRAINT FK_Messages_Conversations REFERENCES dbo.Conversations(Id),
    Role           NVARCHAR(20)  NOT NULL,   -- user | assistant | owner | system
    Content        NVARCHAR(MAX) NOT NULL,
    ExternalId     NVARCHAR(200) NULL,       -- channel message id, used to ignore webhook retries
    CreatedAt      DATETIME2     NOT NULL CONSTRAINT DF_Messages_CreatedAt DEFAULT SYSUTCDATETIME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Messages_ExternalId')
CREATE UNIQUE INDEX UX_Messages_ExternalId ON dbo.Messages(ExternalId) WHERE ExternalId IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Messages_Conversation')
CREATE INDEX IX_Messages_Conversation ON dbo.Messages(ConversationId, Id);
GO

IF OBJECT_ID('dbo.Orders') IS NULL
CREATE TABLE dbo.Orders (
    Id                     INT IDENTITY(1,1) PRIMARY KEY,
    ConversationId         INT            NOT NULL CONSTRAINT FK_Orders_Conversations REFERENCES dbo.Conversations(Id),
    Status                 NVARCHAR(30)   NOT NULL,
    Subtotal               DECIMAL(12,2)  NOT NULL CONSTRAINT DF_Orders_Subtotal DEFAULT 0,
    DeliveryFee            DECIMAL(12,2)  NOT NULL CONSTRAINT DF_Orders_DeliveryFee DEFAULT 0,
    Total                  DECIMAL(12,2)  NOT NULL CONSTRAINT DF_Orders_Total DEFAULT 0,
    DeliveryArea           NVARCHAR(200)  NULL,
    DeliveryNotes          NVARCHAR(500)  NULL,
    Latitude               FLOAT          NULL,
    Longitude              FLOAT          NULL,
    PaymentPhone           NVARCHAR(20)   NULL,
    MpesaCheckoutRequestId NVARCHAR(100)  NULL,
    MpesaReceipt           NVARCHAR(50)   NULL,
    LastPaymentError       NVARCHAR(300)  NULL,
    CreatedAt              DATETIME2      NOT NULL CONSTRAINT DF_Orders_CreatedAt DEFAULT SYSUTCDATETIME(),
    PaidAt                 DATETIME2      NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Orders_Checkout')
CREATE INDEX IX_Orders_Checkout ON dbo.Orders(MpesaCheckoutRequestId);
GO

IF OBJECT_ID('dbo.OrderLines') IS NULL
CREATE TABLE dbo.OrderLines (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    OrderId     INT           NOT NULL CONSTRAINT FK_OrderLines_Orders REFERENCES dbo.Orders(Id),
    ProductId   INT           NOT NULL CONSTRAINT FK_OrderLines_Products REFERENCES dbo.Products(Id),
    ProductName NVARCHAR(200) NOT NULL,
    UnitPrice   DECIMAL(12,2) NOT NULL,
    Quantity    INT           NOT NULL,
    CONSTRAINT UQ_OrderLines_Order_Product UNIQUE (OrderId, ProductId)
);
GO

-- Demo catalogue (generic, no real brand). Replace with the pilot business's real products.
IF NOT EXISTS (SELECT 1 FROM dbo.Products)
INSERT INTO dbo.Products (Name, Description, Category, Price, Stock) VALUES
 (N'Matte Liquid Lipstick - Cocoa',      N'Long-wear matte liquid lipstick, deep brown shade',              N'Lips',     1200, 40),
 (N'Matte Liquid Lipstick - Ruby',       N'Long-wear matte liquid lipstick, classic red',                   N'Lips',     1200, 35),
 (N'Lip Gloss - Honey Glow',             N'Non-sticky shimmer gloss',                                       N'Lips',      850, 50),
 (N'Liquid Foundation 30ml - Shade 8',   N'Medium coverage foundation for deep skin tones',                 N'Face',     2400, 20),
 (N'Liquid Foundation 30ml - Shade 10',  N'Medium coverage foundation for very deep skin tones',            N'Face',     2400, 15),
 (N'Pressed Powder - Mocha',             N'Oil-control setting powder',                                     N'Face',     1500, 25),
 (N'Concealer - Espresso',               N'Creamy full-coverage concealer',                                 N'Face',     1100, 30),
 (N'Eyeshadow Palette - Savannah',       N'12 warm earth-tone shades, matte and shimmer',                   N'Eyes',     2800, 12),
 (N'Volumising Mascara',                 N'Black, smudge-proof',                                            N'Eyes',      950, 45),
 (N'Brow Pencil - Dark Brown',           N'Fine-tip pencil with spoolie',                                   N'Eyes',      600, 60),
 (N'Setting Spray 100ml',                N'Keeps makeup in place up to 12 hours',                           N'Face',     1300, 22),
 (N'Makeup Brush Set (8 pcs)',           N'Soft synthetic brushes with pouch',                              N'Tools',    2200, 10),
 (N'Beauty Blender Sponge',              N'Latex-free blending sponge',                                     N'Tools',     450, 80),
 (N'Shea Butter Body Lotion 400ml',      N'Rich moisturising lotion with shea butter',                      N'Skincare', 1350, 30),
 (N'Vitamin C Face Serum 30ml',          N'Brightening serum for dark spots',                               N'Skincare', 1900, 18);
GO
