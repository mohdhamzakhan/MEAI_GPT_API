using MEAI_GPT_API.Controller;
using Microsoft.Extensions.Options;

namespace MEAI_GPT_API.Models
{

    public class OracleEBSQuery
    {
        private readonly ILogger<OracleEBSQuery> _logger;
        private readonly IOptions<ChromaDbOptions> _chromaOptions;
        public OracleEBSQuery(ILogger<OracleEBSQuery> logger, IOptions<ChromaDbOptions> chromaOptions)
        {
            _logger = logger;
            _chromaOptions = chromaOptions;
            EnsureOracleEBSSchemaContext();
        }

        public bool IsOracleEBSQuery(string question)
        {
            if (string.IsNullOrWhiteSpace(question))
                return false;

            var q = question.ToLowerInvariant();

            // 1️⃣ SQL/Query intent keywords (flexible matching)
            var queryIntentPatterns = new[]
            {
        "write query",
        "write a query",
        "write sql",
        "write a sql",
        "sql query",
        "create query",
        "generate query",
        "query for",
        "query to",
        "how to query",
        "show me query",
        "give me query",
        "need query",
        "get query"
    };

            if (queryIntentPatterns.Any(p => q.Contains(p)))
                return true;

            // 2️⃣ Direct SQL keywords
            var sqlKeywords = new[] { " sql ", "select ", "from ", "where ", "join " };
            if (sqlKeywords.Any(k => q.Contains(k)))
                return true;

            // 3️⃣ Oracle EBS specific terms
            var oracleEbsKeywords = new[]
            {
        "oracle", "ebs", "r12",
        "on hand", "onhand", "on-hand",
        "purchase order", "sales order", "work order",
        "po ", " po", "receipt", "shipment",
        "vendor", "supplier", "customer",
        "invoice", "payment", "ledger",
        "inventory", "item", "organization",
        "employee", "payroll"
    };

            if (oracleEbsKeywords.Any(k => q.Contains(k)))
            {
                // If Oracle/EBS terms are present AND it's asking for data/info, likely a query
                var dataRequestKeywords = new[]
                {
            "get", "fetch", "retrieve", "show", "list", "find",
            "all ", "data", "information", "details"
        };

                if (dataRequestKeywords.Any(k => q.Contains(k)))
                    return true;
            }

            // 4️⃣ Oracle table prefixes (strong indicator)
            var oracleTablePrefixes = new[]
            {
        "mtl_", "po_", "oe_", "ap_", "ar_", "gl_", "wip_",
        "hr_", "per_", "fnd_", "inv_", "wsh_"
    };

            if (oracleTablePrefixes.Any(p => q.Contains(p)))
                return true;

            return false;
        }

        private void CreateOracleEBSInventoryContext(string folder)
        {
            var filePath = Path.Combine(folder, "oracle-ebs-inventory.txt");
            if (File.Exists(filePath)) return;

            var content = @"ORACLE E-BUSINESS SUITE R12i - INVENTORY MODULE

=== CORE INVENTORY TABLES ===

MTL_SYSTEM_ITEMS_B - Item Master
- INVENTORY_ITEM_ID: Unique item identifier
- ORGANIZATION_ID: Organization identifier
- SEGMENT1: Item number/code
- DESCRIPTION: Item description (in MTL_SYSTEM_ITEMS_TL for translations)
- PRIMARY_UOM_CODE: Primary unit of measure
- ITEM_TYPE: Type of item
- INVENTORY_ITEM_STATUS_CODE: Active/Inactive status
- LOT_CONTROL_CODE: 1=Not controlled, 2=Lot controlled
- SERIAL_NUMBER_CONTROL_CODE: 1=No control, 2=Predefined, 6=Dynamic
- PLANNING_MAKE_BUY_CODE: 1=Make, 2=Buy
- LIST_PRICE_PER_UNIT: Standard list price
- WEIGHT_UOM_CODE: Weight unit of measure
- UNIT_WEIGHT: Weight per unit
- VOLUME_UOM_CODE: Volume unit of measure
- UNIT_VOLUME: Volume per unit

MTL_ONHAND_QUANTITIES - On-Hand Inventory
- INVENTORY_ITEM_ID, ORGANIZATION_ID: Item and org
- SUBINVENTORY_CODE: Subinventory location
- LOCATOR_ID: Storage locator
- LOT_NUMBER: Lot number (if lot controlled)
- REVISION: Item revision
- TRANSACTION_QUANTITY: Available quantity
- PRIMARY_TRANSACTION_QUANTITY: Quantity in primary UOM
- COST_GROUP_ID: Cost group (for WMS)
- LPN_ID: License plate number
- IS_CONSIGNED: Consignment flag (1=Yes, 2=No)

MTL_MATERIAL_TRANSACTIONS - All Inventory Transactions
- TRANSACTION_ID: Unique transaction ID
- INVENTORY_ITEM_ID, ORGANIZATION_ID
- TRANSACTION_TYPE_ID: Type of transaction
- TRANSACTION_DATE: When transaction occurred
- TRANSACTION_QUANTITY: Quantity moved
- TRANSACTION_UOM: Unit of measure
- SUBINVENTORY_CODE: From/To subinventory
- LOCATOR_ID: Storage location
- TRANSFER_SUBINVENTORY: Transfer destination
- TRANSFER_LOCATOR_ID: Transfer destination locator
- SOURCE_CODE: Transaction source (e.g., 'PO' for Purchase Order)
- SOURCE_LINE_ID: Source document line ID
- ACTUAL_COST: Actual transaction cost
- TRANSACTION_COST: Standard transaction cost

MTL_TRANSACTION_TYPES - Transaction Type Definitions
- TRANSACTION_TYPE_ID
- TRANSACTION_TYPE_NAME: Descriptive name
- TRANSACTION_ACTION_ID: Action performed
- TRANSACTION_SOURCE_TYPE_ID: Source system type

MTL_SECONDARY_INVENTORIES - Subinventory Definitions
- SECONDARY_INVENTORY_NAME: Subinventory code
- ORGANIZATION_ID
- DESCRIPTION: Subinventory description
- LOCATOR_TYPE: 1=No control, 2=Prespecified, 3=Dynamic
- ASSET_INVENTORY: 1=Asset, 2=Expense
- QUANTITY_TRACKED: 1=Yes, 2=No
- RESERVABLE_TYPE: 1=Yes, 2=No

MTL_ITEM_LOCATIONS - Storage Locators
- INVENTORY_LOCATION_ID
- ORGANIZATION_ID
- SEGMENT1, SEGMENT2, SEGMENT3: Locator segments (e.g., Row-Rack-Bin)
- DESCRIPTION: Locator description
- SUBINVENTORY_CODE: Parent subinventory
- LOCATION_MAXIMUM_UNITS: Max capacity
- STATUS_ID: Active/Inactive status

MTL_LOT_NUMBERS - Lot Control
- LOT_NUMBER
- INVENTORY_ITEM_ID, ORGANIZATION_ID
- EXPIRATION_DATE: When lot expires
- ORIGINATION_DATE: When lot was created
- PARENT_LOT_NUMBER: Parent lot (if split)
- GRADE_CODE: Lot grade/quality
- STATUS_ID: Lot status

MTL_SERIAL_NUMBERS - Serial Number Control
- SERIAL_NUMBER
- INVENTORY_ITEM_ID, CURRENT_ORGANIZATION_ID
- CURRENT_SUBINVENTORY_CODE
- CURRENT_LOCATOR_ID
- CURRENT_STATUS: 1=Defined not used, 3=Resides in stores, 4=Issued out, 5=Intransit, 6=Resides in receiving
- LOT_NUMBER: Associated lot
- LAST_TRANSACTION_ID: Last transaction affecting this serial

MTL_ITEM_CATEGORIES_V - Item Categories (View)
- INVENTORY_ITEM_ID, ORGANIZATION_ID
- CATEGORY_SET_NAME: Category grouping
- CATEGORY_CONCAT_SEGS: Concatenated category value

MTL_UNITS_OF_MEASURE - UOM Definitions
- UOM_CODE: Unit of measure code
- UNIT_OF_MEASURE: Full UOM name
- UOM_CLASS: Class (Weight, Volume, Length, etc.)
- BASE_UOM_FLAG: Y if base UOM for class

=== COMMON INVENTORY QUERIES ===

## Get Item On-Hand by Organization
SELECT 
    msi.segment1 item_code,
    msi.description,
    moq.subinventory_code,
    mil.segment1||'.'||mil.segment2||'.'||mil.segment3 locator,
    moq.lot_number,
    SUM(moq.transaction_quantity) onhand_qty,
    msi.primary_uom_code uom
FROM mtl_onhand_quantities moq
JOIN mtl_system_items_b msi 
    ON moq.inventory_item_id = msi.inventory_item_id
    AND moq.organization_id = msi.organization_id
LEFT JOIN mtl_item_locations mil
    ON moq.locator_id = mil.inventory_location_id
WHERE moq.organization_id = :p_org_id
GROUP BY msi.segment1, msi.description, moq.subinventory_code,
         mil.segment1, mil.segment2, mil.segment3, 
         moq.lot_number, msi.primary_uom_code;

## Item Transaction History
SELECT 
    mmt.transaction_date,
    msi.segment1 item_code,
    mtt.transaction_type_name,
    mmt.transaction_quantity,
    mmt.transaction_uom,
    mmt.subinventory_code,
    mmt.transfer_subinventory,
    mmt.actual_cost
FROM mtl_material_transactions mmt
JOIN mtl_system_items_b msi
    ON mmt.inventory_item_id = msi.inventory_item_id
    AND mmt.organization_id = msi.organization_id
JOIN mtl_transaction_types mtt
    ON mmt.transaction_type_id = mtt.transaction_type_id
WHERE mmt.organization_id = :p_org_id
  AND mmt.transaction_date BETWEEN :p_from_date AND :p_to_date
ORDER BY mmt.transaction_date DESC;

## Serial Numbers by Item
SELECT 
    msn.serial_number,
    msi.segment1 item_code,
    msn.current_subinventory_code,
    msn.current_status,
    msn.lot_number,
    DECODE(msn.current_status,
           1, 'Defined Not Used',
           3, 'In Stores',
           4, 'Issued',
           5, 'In-Transit',
           6, 'In Receiving') status_name
FROM mtl_serial_numbers msn
JOIN mtl_system_items_b msi
    ON msn.inventory_item_id = msi.inventory_item_id
WHERE msn.current_organization_id = :p_org_id
  AND msi.segment1 = :p_item_code;";

            File.WriteAllText(filePath, content);
            _logger.LogInformation("Created Oracle EBS Inventory schema context");
        }

        private void CreateOracleEBSPurchasingContext(string folder)
        {
            var filePath = Path.Combine(folder, "oracle-ebs-purchasing.txt");
            if (File.Exists(filePath)) return;

            var content = @"ORACLE E-BUSINESS SUITE R12i - PURCHASING MODULE

=== CORE PURCHASING TABLES ===

PO_HEADERS_ALL - Purchase Order Headers
- PO_HEADER_ID: Unique PO identifier
- SEGMENT1: PO Number
- TYPE_LOOKUP_CODE: STANDARD, BLANKET, CONTRACT, PLANNED
- ORG_ID: Operating unit
- VENDOR_ID: Supplier identifier
- VENDOR_SITE_ID: Supplier site
- CURRENCY_CODE: PO currency
- AUTHORIZATION_STATUS: APPROVED, INCOMPLETE, REJECTED, etc.
- APPROVED_FLAG: Y/N
- APPROVED_DATE
- CREATION_DATE
- CREATED_BY
- COMMENTS: Header level comments

PO_LINES_ALL - Purchase Order Lines
- PO_LINE_ID: Unique line identifier
- PO_HEADER_ID: Parent PO
- LINE_NUM: Line number
- ITEM_ID: Inventory item ID
- ITEM_DESCRIPTION: Line description
- UNIT_MEAS_LOOKUP_CODE: UOM
- UNIT_PRICE: Price per unit
- QUANTITY: Ordered quantity
- QUANTITY_RECEIVED: Received quantity
- QUANTITY_BILLED: Invoiced quantity
- QUANTITY_CANCELLED: Cancelled quantity
- CLOSED_FLAG: Y/N
- CANCEL_FLAG: Y/N
- NEED_BY_DATE: Required delivery date

PO_LINE_LOCATIONS_ALL - Purchase Order Shipments
- LINE_LOCATION_ID: Shipment identifier
- PO_HEADER_ID, PO_LINE_ID: Parent PO and line
- SHIPMENT_NUM: Shipment number
- SHIP_TO_LOCATION_ID: Delivery location
- SHIP_TO_ORGANIZATION_ID: Destination org
- QUANTITY: Shipment quantity
- QUANTITY_RECEIVED: Received quantity
- QUANTITY_ACCEPTED: Accepted quantity
- QUANTITY_REJECTED: Rejected quantity
- QUANTITY_BILLED: Invoiced quantity
- QUANTITY_CANCELLED: Cancelled quantity
- PROMISED_DATE: Supplier promised date
- NEED_BY_DATE: Required date
- PRICE_OVERRIDE: Override price
- CLOSED_CODE: CLOSED, OPEN, CLOSED FOR RECEIVING, etc.
- RECEIPT_REQUIRED_FLAG: Y/N
- INSPECTION_REQUIRED_FLAG: Y/N

PO_DISTRIBUTIONS_ALL - Purchase Order Distributions (Accounting)
- PO_DISTRIBUTION_ID
- PO_HEADER_ID, PO_LINE_ID, LINE_LOCATION_ID
- DISTRIBUTION_NUM: Distribution number
- QUANTITY_ORDERED: Distributed quantity
- QUANTITY_DELIVERED: Received quantity
- QUANTITY_BILLED: Invoiced quantity
- QUANTITY_CANCELLED: Cancelled quantity
- CODE_COMBINATION_ID: Accounting flexfield
- BUDGET_ACCOUNT_ID: Budget account
- DESTINATION_TYPE_CODE: EXPENSE, INVENTORY, SHOP FLOOR
- DESTINATION_ORGANIZATION_ID
- DESTINATION_SUBINVENTORY: Target subinventory

PO_VENDORS - Supplier Master
- VENDOR_ID
- VENDOR_NAME: Supplier name
- SEGMENT1: Supplier number
- VENDOR_TYPE_LOOKUP_CODE: Supplier classification
- ENABLED_FLAG: Y/N
- PAYMENT_TERMS_ID: Default payment terms

PO_VENDOR_SITES_ALL - Supplier Sites
- VENDOR_SITE_ID
- VENDOR_ID: Parent supplier
- VENDOR_SITE_CODE: Site code
- ORG_ID: Operating unit
- PURCHASING_SITE_FLAG: Y/N (can purchase from this site)
- PAY_SITE_FLAG: Y/N (can pay at this site)
- SHIP_TO_LOCATION_ID: Default ship-to

RCV_SHIPMENT_HEADERS - Receipt Headers
- SHIPMENT_HEADER_ID
- SHIPMENT_NUM: Receipt number
- RECEIPT_SOURCE_CODE: VENDOR, INVENTORY, CUSTOMER
- VENDOR_ID
- SHIP_TO_ORG_ID: Receiving organization

RCV_SHIPMENT_LINES - Receipt Lines
- SHIPMENT_LINE_ID
- SHIPMENT_HEADER_ID
- LINE_NUM
- ITEM_ID
- ITEM_DESCRIPTION
- QUANTITY_SHIPPED: Shipped quantity
- QUANTITY_RECEIVED: Received quantity
- UNIT_OF_MEASURE

RCV_TRANSACTIONS - Receipt Transactions
- TRANSACTION_ID
- TRANSACTION_TYPE: RECEIVE, DELIVER, RETURN TO VENDOR, etc.
- TRANSACTION_DATE
- SHIPMENT_HEADER_ID, SHIPMENT_LINE_ID
- PO_HEADER_ID, PO_LINE_ID, PO_LINE_LOCATION_ID
- QUANTITY: Transaction quantity
- UNIT_OF_MEASURE
- DESTINATION_TYPE_CODE: INVENTORY, EXPENSE
- SUBINVENTORY: Destination subinventory
- LOCATOR_ID: Destination locator

=== COMMON PURCHASING QUERIES ===

## Purchase Orders by Supplier
SELECT 
    poh.segment1 po_number,
    poh.type_lookup_code po_type,
    pv.vendor_name supplier,
    pvs.vendor_site_code site,
    poh.currency_code,
    poh.authorization_status status,
    poh.creation_date,
    SUM(pol.quantity * pol.unit_price) total_amount
FROM po_headers_all poh
JOIN po_vendors pv ON poh.vendor_id = pv.vendor_id
JOIN po_vendor_sites_all pvs ON poh.vendor_site_id = pvs.vendor_site_id
JOIN po_lines_all pol ON poh.po_header_id = pol.po_header_id
WHERE poh.org_id = :p_org_id
  AND pv.vendor_name LIKE :p_supplier_name
GROUP BY poh.segment1, poh.type_lookup_code, pv.vendor_name,
         pvs.vendor_site_code, poh.currency_code, 
         poh.authorization_status, poh.creation_date;

## PO Receipt Status
SELECT 
    poh.segment1 po_number,
    pol.line_num,
    msi.segment1 item_code,
    pol.item_description,
    pll.quantity ordered_qty,
    pll.quantity_received received_qty,
    pll.quantity_billed invoiced_qty,
    (pll.quantity - pll.quantity_received) pending_receipt,
    pll.need_by_date,
    pll.closed_code
FROM po_headers_all poh
JOIN po_lines_all pol ON poh.po_header_id = pol.po_header_id
JOIN po_line_locations_all pll ON pol.po_line_id = pll.po_line_id
LEFT JOIN mtl_system_items_b msi 
    ON pol.item_id = msi.inventory_item_id
    AND pll.ship_to_organization_id = msi.organization_id
WHERE poh.segment1 = :p_po_number
ORDER BY pol.line_num, pll.shipment_num;

## Receipts by Date Range
SELECT 
    rsh.shipment_num receipt_number,
    rsh.receipt_num,
    rt.transaction_date,
    poh.segment1 po_number,
    msi.segment1 item_code,
    rt.quantity,
    rt.unit_of_measure,
    rt.subinventory,
    rt.transaction_type
FROM rcv_transactions rt
JOIN rcv_shipment_headers rsh ON rt.shipment_header_id = rsh.shipment_header_id
JOIN po_headers_all poh ON rt.po_header_id = poh.po_header_id
LEFT JOIN mtl_system_items_b msi 
    ON rt.item_id = msi.inventory_item_id
    AND rt.organization_id = msi.organization_id
WHERE rt.transaction_date BETWEEN :p_from_date AND :p_to_date
  AND rt.organization_id = :p_org_id
ORDER BY rt.transaction_date DESC;";

            File.WriteAllText(filePath, content);
            _logger.LogInformation("Created Oracle EBS Purchasing schema context");
        }

        private void CreateOracleEBSOrderManagementContext(string folder)
        {
            var filePath = Path.Combine(folder, "oracle-ebs-order-management.txt");
            if (File.Exists(filePath)) return;

            var content = @"ORACLE E-BUSINESS SUITE R12i - ORDER MANAGEMENT MODULE

=== CORE ORDER MANAGEMENT TABLES ===

OE_ORDER_HEADERS_ALL - Sales Order Headers
- HEADER_ID: Unique order identifier
- ORDER_NUMBER: Order number
- ORDERED_DATE: Order date
- ORDER_TYPE_ID: Order type
- PRICE_LIST_ID: Price list
- SOLD_TO_ORG_ID: Customer identifier
- SHIP_TO_ORG_ID: Ship-to customer
- INVOICE_TO_ORG_ID: Bill-to customer
- SALESREP_ID: Sales representative
- PAYMENT_TERM_ID: Payment terms
- FLOW_STATUS_CODE: ENTERED, BOOKED, CLOSED, CANCELLED
- BOOKED_FLAG: Y/N
- CANCELLED_FLAG: Y/N
- CREATION_DATE
- ORG_ID: Operating unit

OE_ORDER_LINES_ALL - Sales Order Lines
- LINE_ID: Unique line identifier
- HEADER_ID: Parent order
- LINE_NUMBER: Line number
- INVENTORY_ITEM_ID: Item
- ORDERED_QUANTITY: Ordered qty
- SHIPPED_QUANTITY: Shipped qty
- INVOICED_QUANTITY: Invoiced qty
- CANCELLED_QUANTITY: Cancelled qty
- UNIT_SELLING_PRICE: Selling price
- UNIT_LIST_PRICE: List price
- ORDER_QUANTITY_UOM: UOM
- SHIP_FROM_ORG_ID: Shipping warehouse
- SHIP_TO_ORG_ID: Customer ship-to
- FLOW_STATUS_CODE: ENTERED, AWAITING_SHIPPING, SHIPPED, etc.
- SCHEDULE_SHIP_DATE: Planned ship date
- ACTUAL_SHIPMENT_DATE: Actual ship date
- REQUEST_DATE: Customer requested date
- PROMISE_DATE: Promised delivery date
- CANCELLED_FLAG: Y/N
- RETURN_REASON_CODE: Return reason (for RMAs)

OE_ORDER_LINES_V - Sales Order Lines View (with details)
- Combines OE_ORDER_LINES_ALL with item and customer info
- Includes calculated fields and descriptions

OE_TRANSACTION_TYPES_TL - Order Types
- TRANSACTION_TYPE_ID
- NAME: Order type name (Sales Order, Return, etc.)
- LANGUAGE

HZ_CUST_ACCOUNTS - Customer Accounts
- CUST_ACCOUNT_ID
- ACCOUNT_NUMBER: Customer number
- ACCOUNT_NAME: Customer name
- PARTY_ID: Link to HZ_PARTIES
- STATUS: Active/Inactive

HZ_CUST_SITE_USES_ALL - Customer Sites
- SITE_USE_ID
- CUST_ACCOUNT_ID
- SITE_USE_CODE: BILL_TO, SHIP_TO, etc.
- LOCATION: Site address
- PRIMARY_FLAG: Y/N
- STATUS: Active/Inactive

WSH_DELIVERY_DETAILS - Shipping Details
- DELIVERY_DETAIL_ID
- SOURCE_HEADER_ID: Order header ID
- SOURCE_LINE_ID: Order line ID
- REQUESTED_QUANTITY
- SHIPPED_QUANTITY
- DELIVERED_QUANTITY
- RELEASED_STATUS: R=Ready, Y=Staged, C=Shipped, D=Delivered
- INV_INTERFACED_FLAG: Y/N (interfaced to inventory)
- OE_INTERFACED_FLAG: Y/N (interfaced back to OM)

WSH_NEW_DELIVERIES - Delivery Headers
- DELIVERY_ID
- NAME: Delivery number
- STATUS_CODE: OP=Open, CL=Closed, IT=In-transit, etc.
- INITIAL_PICKUP_DATE
- ULTIMATE_DROPOFF_DATE
- WAYBILL: Shipping document number

=== COMMON ORDER MANAGEMENT QUERIES ===

## Sales Orders by Customer
SELECT 
    ooh.order_number,
    ooh.ordered_date,
    ott.name order_type,
    hca.account_number customer_number,
    hca.account_name customer_name,
    ooh.flow_status_code status,
    SUM(ool.ordered_quantity * ool.unit_selling_price) order_total
FROM oe_order_headers_all ooh
JOIN oe_order_lines_all ool ON ooh.header_id = ool.header_id
JOIN oe_transaction_types_tl ott ON ooh.order_type_id = ott.transaction_type_id
JOIN hz_cust_accounts hca ON ooh.sold_to_org_id = hca.cust_account_id
WHERE ooh.org_id = :p_org_id
  AND hca.account_number = :p_customer_number
  AND ott.language = 'US'
GROUP BY ooh.order_number, ooh.ordered_date, ott.name,
         hca.account_number, hca.account_name, ooh.flow_status_code;

## Order Line Details with Ship Status
SELECT 
    ooh.order_number,
    ool.line_number,
    msi.segment1 item_code,
    ool.ordered_quantity,
    ool.shipped_quantity,
    (ool.ordered_quantity - NVL(ool.shipped_quantity,0)) pending_ship,
    ool.unit_selling_price,
    ool.flow_status_code line_status,
    ool.schedule_ship_date,
    ool.promise_date
FROM oe_order_headers_all ooh
JOIN oe_order_lines_all ool ON ooh.header_id = ool.header_id
LEFT JOIN mtl_system_items_b msi 
    ON ool.inventory_item_id = msi.inventory_item_id
    AND ool.ship_from_org_id = msi.organization_id
WHERE ooh.order_number = :p_order_number
ORDER BY ool.line_number;

## Delivery Status
SELECT 
    wnd.name delivery_number,
    ooh.order_number,
    wnd.status_code delivery_status,
    wnd.initial_pickup_date pickup_date,
    wnd.ultimate_dropoff_date dropoff_date,
    wdd.requested_quantity,
    wdd.shipped_quantity,
    wdd.released_status
FROM wsh_new_deliveries wnd
JOIN wsh_delivery_assignments wda ON wnd.delivery_id = wda.delivery_id
JOIN wsh_delivery_details wdd ON wda.delivery_detail_id = wdd.delivery_detail_id
JOIN oe_order_headers_all ooh ON wdd.source_header_id = ooh.header_id
WHERE wnd.name = :p_delivery_number;";

            File.WriteAllText(filePath, content);
            _logger.LogInformation("Created Oracle EBS Order Management schema context");
        }

        private void CreateOracleEBSAccountsPayableContext(string folder)
        {
            var filePath = Path.Combine(folder, "oracle-ebs-accounts-payable.txt");
            if (File.Exists(filePath)) return;

            var content = @"ORACLE E-BUSINESS SUITE R12i - ACCOUNTS PAYABLE MODULE

=== CORE AP TABLES ===

AP_INVOICES_ALL - Invoice Headers
- INVOICE_ID, INVOICE_NUM, VENDOR_ID, VENDOR_SITE_ID
- INVOICE_AMOUNT, INVOICE_DATE, INVOICE_CURRENCY_CODE
- PAYMENT_STATUS_FLAG, CANCELLED_DATE
- GL_DATE, ACCTS_PAY_CODE_COMBINATION_ID

AP_INVOICE_LINES_ALL - Invoice Lines  
- INVOICE_ID, LINE_NUMBER, LINE_TYPE_LOOKUP_CODE
- AMOUNT, QUANTITY_INVOICED, UNIT_PRICE
- PO_HEADER_ID, PO_LINE_ID, PO_LINE_LOCATION_ID

AP_INVOICE_DISTRIBUTIONS_ALL - Accounting Distributions
- INVOICE_ID, DISTRIBUTION_LINE_NUMBER
- AMOUNT, DIST_CODE_COMBINATION_ID
- PO_DISTRIBUTION_ID

AP_CHECKS_ALL - Payment Headers
- CHECK_ID, CHECK_NUMBER, VENDOR_ID
- CHECK_DATE, AMOUNT, CURRENCY_CODE, STATUS_LOOKUP_CODE

AP_INVOICE_PAYMENTS_ALL - Payment Applications
- INVOICE_ID, CHECK_ID, AMOUNT

=== COMMON AP QUERIES ===

## Invoice Status by Vendor
SELECT 
    ai.invoice_num,
    ai.invoice_date,
    pv.vendor_name,
    ai.invoice_amount,
    ai.amount_paid,
    (ai.invoice_amount - NVL(ai.amount_paid,0)) balance,
    ai.payment_status_flag
FROM ap_invoices_all ai
JOIN po_vendors pv ON ai.vendor_id = pv.vendor_id
WHERE pv.vendor_name LIKE :p_vendor_name
ORDER BY ai.invoice_date DESC;";

            File.WriteAllText(filePath, content);
            _logger.LogInformation("Created Oracle EBS AP schema context");
        }

        private void EnsureOracleEBSSchemaContext()
        {
            var folder = _chromaOptions.Value.PolicyFolder;

            // 1. INVENTORY MODULE
            CreateOracleEBSInventoryContext(folder);

            // 2. PURCHASING MODULE
            CreateOracleEBSPurchasingContext(folder);

            // 3. ORDER MANAGEMENT MODULE
            CreateOracleEBSOrderManagementContext(folder);

            // 4. ACCOUNTS PAYABLE
            CreateOracleEBSAccountsPayableContext(folder);

            // 5. ACCOUNTS RECEIVABLE
            CreateOracleEBSAccountsReceivableContext(folder);

            // 6. GENERAL LEDGER
            CreateOracleEBSGeneralLedgerContext(folder);

            // 7. FIXED ASSETS
            //CreateOracleEBSFixedAssetsContext(folder);

            // 8. HR & PAYROLL
            CreateOracleEBSHRContext(folder);

            // 9. MANUFACTURING (WIP)
            CreateOracleEBSManufacturingContext(folder);

            //// 10. QUALITY MANAGEMENT
            //CreateOracleEBSQualityContext(folder);
        }

        private void CreateOracleEBSManufacturingContext(string folder)
        {
            var filePath = Path.Combine(folder, "oracle-ebs-manufacturing-wip.txt");
            if (File.Exists(filePath)) return;

            var content = @"ORACLE E-BUSINESS SUITE R12i - WORK IN PROCESS (MANUFACTURING) MODULE

=== CORE WIP TABLES ===

WIP_ENTITIES - Work Order/Job Master
Purpose: Stores discrete job and repetitive schedule information
Key Columns:
- WIP_ENTITY_ID: Job identifier (Primary Key)
- WIP_ENTITY_NAME: Job/work order number
- ORGANIZATION_ID: Manufacturing organization
- ENTITY_TYPE: 1=Discrete, 2=Repetitive, 4=Flow
- DESCRIPTION: Job description
- PRIMARY_ITEM_ID: Assembly item being manufactured
- CREATION_DATE, CREATED_BY

WIP_DISCRETE_JOBS - Discrete Job Details
Purpose: Stores discrete manufacturing job information
Key Columns:
- WIP_ENTITY_ID: Job identifier (Primary Key)
- ORGANIZATION_ID: Manufacturing org
- PRIMARY_ITEM_ID: Assembly item
- JOB_TYPE: 1=Standard, 3=Nonstandard, 5=Rework
- STATUS_TYPE: 1=Unreleased, 3=Released, 4=Complete, 5=Complete-No Charge, 7=Cancelled, 12=Closed
- START_QUANTITY: Planned start quantity
- QUANTITY_COMPLETED: Completed quantity
- QUANTITY_SCRAPPED: Scrapped quantity
- NET_QUANTITY: Net quantity (start - scrap)
- SCHEDULED_START_DATE: Planned start date
- SCHEDULED_COMPLETION_DATE: Planned completion date
- DATE_RELEASED: Actual release date
- DATE_COMPLETED: Actual completion date
- DATE_CLOSED: Job close date
- CLASS_CODE: Job/accounting class
- DEMAND_CLASS: Demand class
- COMPLETION_SUBINVENTORY: Completion subinventory
- COMPLETION_LOCATOR_ID: Completion locator
- CREATION_DATE, CREATED_BY

WIP_OPERATIONS - Job Operations
Purpose: Stores routing operations for jobs
Key Columns:
- WIP_ENTITY_ID, ORGANIZATION_ID: Job identifiers
- OPERATION_SEQ_NUM: Operation sequence
- DEPARTMENT_ID: Department/work center
- DESCRIPTION: Operation description
- QUANTITY_IN_QUEUE, QUANTITY_RUNNING, QUANTITY_WAITING_TO_MOVE, QUANTITY_REJECTED, QUANTITY_SCRAPPED, QUANTITY_COMPLETED
- FIRST_UNIT_START_DATE, LAST_UNIT_COMPLETION_DATE
- CREATION_DATE, CREATED_BY

WIP_REQUIREMENT_OPERATIONS - Job Material Requirements
Purpose: Stores material components required for jobs
Key Columns:
- WIP_ENTITY_ID, ORGANIZATION_ID: Job identifiers
- OPERATION_SEQ_NUM: Issuing operation
- INVENTORY_ITEM_ID: Component item
- REQUIRED_QUANTITY: Quantity required
- QUANTITY_ISSUED: Quantity issued
- QUANTITY_PER_ASSEMBLY: Per assembly quantity
- COMPONENT_SEQUENCE_ID: Component sequence
- SUPPLY_SUBINVENTORY, SUPPLY_LOCATOR_ID: Supply location
- CREATION_DATE, CREATED_BY

WIP_TRANSACTIONS - Work Order Transactions
Purpose: Records all WIP transactions (completions, material issues, etc.)
Key Columns:
- TRANSACTION_ID: Transaction identifier (Primary Key)
- WIP_ENTITY_ID, ORGANIZATION_ID: Job identifiers
- TRANSACTION_TYPE: 1=WIP issue, 2=WIP return, 3=Assembly completion, etc.
- TRANSACTION_DATE: Transaction date
- PRIMARY_ITEM_ID: Item
- PRIMARY_QUANTITY: Transaction quantity
- TRANSACTION_UOM: UOM
- OPERATION_SEQ_NUM: Operation
- DEPARTMENT_ID: Department
- CREATION_DATE, CREATED_BY

=== COMMON WIP QUERIES ===

-- =====================================================
-- Query: Discrete Job Status
-- Module: Work In Process (Manufacturing)
-- Tables: WIP_DISCRETE_JOBS, WIP_ENTITIES, MTL_SYSTEM_ITEMS_B
-- Parameters: :p_org_id, :p_job_name (optional)
-- =====================================================

SELECT 
    we.wip_entity_name AS job_number,
    msi.segment1 AS assembly_item,
    msi.description,
    wdj.start_quantity,
    wdj.quantity_completed,
    wdj.quantity_scrapped,
    (wdj.start_quantity - wdj.quantity_completed - wdj.quantity_scrapped) AS wip_qty,
    DECODE(wdj.status_type,
           1, 'Unreleased',
           3, 'Released',
           4, 'Complete',
           5, 'Complete-No Charge',
           7, 'Cancelled',
           12, 'Closed') AS job_status,
    wdj.scheduled_start_date,
    wdj.scheduled_completion_date,
    wdj.date_released,
    wdj.date_completed
FROM wip_discrete_jobs wdj
INNER JOIN wip_entities we
    ON wdj.wip_entity_id = we.wip_entity_id
INNER JOIN mtl_system_items_b msi
    ON wdj.primary_item_id = msi.inventory_item_id
    AND wdj.organization_id = msi.organization_id
WHERE wdj.organization_id = :p_org_id
  AND we.wip_entity_name = NVL(:p_job_name, we.wip_entity_name)
ORDER BY wdj.scheduled_completion_date DESC;";
            File.WriteAllText(filePath, content);
            _logger.LogInformation("Created Oracle EBS AP schema context");

        }

        private void CreateOracleEBSHRContext(string folder)
        {
            var filePath = Path.Combine(folder, "oracle-ebs-hr-payroll.txt");
            if (File.Exists(filePath)) return;

            var content = @"ORACLE E-BUSINESS SUITE R12i - HUMAN RESOURCES & PAYROLL MODULE
ORACLE E-BUSINESS SUITE R12i - HUMAN RESOURCES MODULE

=== CORE HR TABLES ===

PER_ALL_PEOPLE_F - Person Master (Date-Tracked)
Purpose: Stores employee and applicant information
Key Columns:
- PERSON_ID: Person identifier (Primary Key)
- EFFECTIVE_START_DATE, EFFECTIVE_END_DATE: Date tracking
- EMPLOYEE_NUMBER: Employee number
- FULL_NAME: Full name
- FIRST_NAME, MIDDLE_NAMES, LAST_NAME: Name components
- DATE_OF_BIRTH: Birth date
- SEX: Gender
- EMAIL_ADDRESS: Email
- NATIONAL_IDENTIFIER: Social Security Number / Tax ID
- PERSON_TYPE_ID: Person type (Employee, Applicant, etc.)
- CURRENT_EMPLOYEE_FLAG, CURRENT_APPLICANT_FLAG, CURRENT_NPW_FLAG

PER_ALL_ASSIGNMENTS_F - Assignment (Date-Tracked)
Purpose: Stores employee job assignments
Key Columns:
- ASSIGNMENT_ID: Assignment identifier (Primary Key)
- PERSON_ID: Employee
- EFFECTIVE_START_DATE, EFFECTIVE_END_DATE: Date tracking
- BUSINESS_GROUP_ID: Business group
- ORGANIZATION_ID: Organization/department
- JOB_ID: Job
- POSITION_ID: Position
- GRADE_ID: Grade
- LOCATION_ID: Work location
- SUPERVISOR_ID: Supervisor
- EMPLOYMENT_CATEGORY: Full-time, Part-time, etc.
- ASSIGNMENT_STATUS_TYPE_ID: Active, Suspended, Terminated
- PRIMARY_FLAG: Y/N (primary assignment)
- ASSIGNMENT_TYPE: E=Employee, C=Contingent worker
- PAYROLL_ID: Payroll

HR_ALL_ORGANIZATION_UNITS - Organizations/Departments
Purpose: Stores organizational units
Key Columns:
- ORGANIZATION_ID: Organization identifier (Primary Key)
- NAME: Organization name
- TYPE: Department, Business Group, etc.
- LOCATION_ID: Location
- BUSINESS_GROUP_ID: Business group

PER_JOBS - Jobs
Purpose: Job definitions
Key Columns:
- JOB_ID: Job identifier (Primary Key)
- NAME: Job name
- BUSINESS_GROUP_ID: Business group

=== COMMON HR QUERIES ===

-- =====================================================
-- Query: Active Employees with Assignment Details
-- Module: Human Resources
-- Tables: PER_ALL_PEOPLE_F, PER_ALL_ASSIGNMENTS_F, HR_ALL_ORGANIZATION_UNITS, PER_JOBS
-- Parameters: :p_business_group_id, :p_as_of_date
-- =====================================================

SELECT 
    ppf.employee_number,
    ppf.full_name,
    ppf.email_address,
    pj.name AS job_title,
    hou.name AS department,
    ppf.date_of_birth,
    paf.assignment_status_type_id
FROM per_all_people_f ppf
INNER JOIN per_all_assignments_f paf
    ON ppf.person_id = paf.person_id
    AND :p_as_of_date BETWEEN paf.effective_start_date AND paf.effective_end_date
LEFT JOIN hr_all_organization_units hou
    ON paf.organization_id = hou.organization_id
LEFT JOIN per_jobs pj
    ON paf.job_id = pj.job_id
WHERE :p_as_of_date BETWEEN ppf.effective_start_date AND ppf.effective_end_date
  AND ppf.business_group_id = :p_business_group_id
  AND ppf.current_employee_flag = 'Y'
  AND paf.primary_flag = 'Y'
  AND paf.assignment_type = 'E'
ORDER BY ppf.employee_number;
";
            File.WriteAllText(filePath, content);
            _logger.LogInformation("Created Oracle EBS HR schema context");

        }

        private void CreateOracleEBSFixedAssetsContext(string folder)
        {
            throw new NotImplementedException();
        }

        private void CreateOracleEBSGeneralLedgerContext(string folder)
        {
            var filePath = Path.Combine(folder, "oracle-ebs-general-ledger.txt");
            if (File.Exists(filePath)) return;

            var content = @"ORACLE E-BUSINESS SUITE R12i - GENERAL LEDGER MODULE
ORACLE E-BUSINESS SUITE R12i - GENERAL LEDGER MODULE

=== CORE GL TABLES ===

GL_CODE_COMBINATIONS - Chart of Accounts
Purpose: Stores valid GL account code combinations
Key Columns:
- CODE_COMBINATION_ID: Account identifier (Primary Key)
- CHART_OF_ACCOUNTS_ID: COA identifier
- SEGMENT1..SEGMENT30: Accounting flexfield segments (Company, Account, Cost Center, etc.)
- CONCATENATED_SEGMENTS: Full account string
- ENABLED_FLAG: Y/N
- SUMMARY_FLAG: Y=Summary, N=Detail posting allowed
- DETAIL_POSTING_ALLOWED_FLAG: Y/N
- ACCOUNT_TYPE: A=Asset, L=Liability, O=Owner's Equity, R=Revenue, E=Expense

GL_BALANCES - Account Balances
Purpose: Stores GL account balances by period
Key Columns:
- LEDGER_ID: Ledger/set of books
- CODE_COMBINATION_ID: Account
- CURRENCY_CODE: Currency
- PERIOD_NAME: Accounting period
- ACTUAL_FLAG: A=Actual, B=Budget, E=Encumbrance
- BEGIN_BALANCE_DR, BEGIN_BALANCE_CR: Opening balances
- PERIOD_NET_DR, PERIOD_NET_CR: Period activity
- ENDING_BALANCE_DR, ENDING_BALANCE_CR: Closing balances

GL_JE_HEADERS - Journal Entry Headers
Purpose: Stores journal entry header information
Key Columns:
- JE_HEADER_ID: Journal header identifier (Primary Key)
- JE_SOURCE: Journal source (Payables, Receivables, etc.)
- JE_CATEGORY: Journal category
- PERIOD_NAME: GL period
- NAME: Journal entry name/number
- DESCRIPTION: Journal description
- STATUS: P=Posted, U=Unposted
- ACTUAL_FLAG: A, B, E
- CURRENCY_CODE: Currency
- LEDGER_ID: Ledger
- CREATION_DATE, CREATED_BY

GL_JE_LINES - Journal Entry Lines
Purpose: Stores journal entry line details
Key Columns:
- JE_HEADER_ID: Parent journal header
- JE_LINE_NUM: Line number
- CODE_COMBINATION_ID: GL account
- ENTERED_DR, ENTERED_CR: Debit/credit amounts in entered currency
- ACCOUNTED_DR, ACCOUNTED_CR: Debit/credit amounts in functional currency
- DESCRIPTION: Line description
- CREATION_DATE, CREATED_BY

GL_PERIODS - Accounting Periods
Purpose: Defines accounting periods
Key Columns:
- PERIOD_SET_NAME: Calendar name
- PERIOD_NAME: Period identifier (e.g., JAN-25, FEB-25)
- PERIOD_TYPE: Month, Quarter, Year
- PERIOD_YEAR: Fiscal year
- PERIOD_NUM: Period number
- START_DATE, END_DATE: Period date range
- CLOSING_STATUS: O=Open, C=Closed, P=Permanently Closed

=== COMMON GL QUERIES ===

-- =====================================================
-- Query: Account Balance by Period
-- Module: General Ledger
-- Tables: GL_BALANCES, GL_CODE_COMBINATIONS
-- Parameters: :p_ledger_id, :p_period_name, :p_account_segment
-- =====================================================

SELECT 
    gcc.concatenated_segments AS account,
    gcc.segment1 AS company,
    gcc.segment2 AS account_code,
    gcc.segment3 AS cost_center,
    glb.period_name,
    glb.begin_balance_dr - glb.begin_balance_cr AS opening_balance,
    glb.period_net_dr - glb.period_net_cr AS period_movement,
    glb.ending_balance_dr - glb.ending_balance_cr AS closing_balance,
    glb.currency_code
FROM gl_balances glb
INNER JOIN gl_code_combinations gcc
    ON glb.code_combination_id = gcc.code_combination_id
WHERE glb.ledger_id = :p_ledger_id
  AND glb.period_name = :p_period_name
  AND glb.actual_flag = 'A'
  AND gcc.segment2 = NVL(:p_account_segment, gcc.segment2)
ORDER BY gcc.concatenated_segments;


-- =====================================================
-- Query: Journal Entry Details
-- Module: General Ledger
-- Tables: GL_JE_HEADERS, GL_JE_LINES, GL_CODE_COMBINATIONS
-- Parameters: :p_ledger_id, :p_period_name
-- =====================================================

SELECT 
    gjh.name AS journal_name,
    gjh.je_source AS source,
    gjh.je_category AS category,
    gjh.period_name,
    gjh.status,
    gjl.je_line_num,
    gcc.concatenated_segments AS account,
    gjl.entered_dr,
    gjl.entered_cr,
    gjl.accounted_dr,
    gjl.accounted_cr,
    gjl.description
FROM gl_je_headers gjh
INNER JOIN gl_je_lines gjl
    ON gjh.je_header_id = gjl.je_header_id
INNER JOIN gl_code_combinations gcc
    ON gjl.code_combination_id = gcc.code_combination_id
WHERE gjh.ledger_id = :p_ledger_id
  AND gjh.period_name = :p_period_name
ORDER BY gjh.name, gjl.je_line_num;
";
            File.WriteAllText(filePath, content);
            _logger.LogInformation("Created Oracle EBS GL schema context");

        }

        private void CreateOracleEBSAccountsReceivableContext(string folder)
        {
            var filePath = Path.Combine(folder, "oracle-ebs-accounts-receivable.txt");
            if (File.Exists(filePath)) return;

            var content = @"ORACLE E-BUSINESS SUITE R12i - ACCOUNTS RECEIVABLE MODULE
ORACLE E-BUSINESS SUITE R12i - ACCOUNTS RECEIVABLE MODULE

=== CORE AR TABLES ===

RA_CUSTOMER_TRX_ALL - Invoice/Transaction Headers
Purpose: Stores customer invoice and credit memo headers
Key Columns:
- CUSTOMER_TRX_ID: Transaction identifier (Primary Key)
- TRX_NUMBER: Invoice/transaction number
- TRX_DATE: Transaction date
- CUST_TRX_TYPE_ID: Transaction type (Invoice, Credit Memo, Debit Memo, etc.)
- BILL_TO_CUSTOMER_ID: Bill-to customer
- BILL_TO_SITE_USE_ID: Bill-to site
- SHIP_TO_CUSTOMER_ID: Ship-to customer
- SHIP_TO_SITE_USE_ID: Ship-to site
- INVOICE_CURRENCY_CODE: Currency
- EXCHANGE_RATE, EXCHANGE_RATE_TYPE, EXCHANGE_DATE
- TERM_ID: Payment terms
- PRIMARY_SALESREP_ID: Sales representative
- INTERFACE_HEADER_CONTEXT: Source system context
- INTERFACE_HEADER_ATTRIBUTE1..15: Source reference fields
- COMPLETE_FLAG: Y/N
- PRINTING_PENDING: Y/N
- PREVIOUS_CUSTOMER_TRX_ID: Related transaction (for credit memos)
- RELATED_CUSTOMER_TRX_ID: Related transaction
- ORG_ID: Operating unit
- COMMENTS: Transaction comments
- CREATION_DATE, CREATED_BY

RA_CUSTOMER_TRX_LINES_ALL - Invoice/Transaction Lines
Purpose: Stores invoice line details
Key Columns:
- CUSTOMER_TRX_LINE_ID: Line identifier (Primary Key)
- CUSTOMER_TRX_ID: Parent transaction
- LINE_NUMBER: Line number
- LINE_TYPE: LINE, TAX, FREIGHT, CHARGES
- INVENTORY_ITEM_ID: Item
- DESCRIPTION: Line description
- QUANTITY_ORDERED: Ordered quantity
- QUANTITY_INVOICED: Invoiced quantity
- QUANTITY_CREDITED: Credited quantity (for credit memos)
- UNIT_SELLING_PRICE: Selling price
- UNIT_STANDARD_PRICE: Standard price
- EXTENDED_AMOUNT: Line amount (quantity × price)
- GROSS_UNIT_SELLING_PRICE: Gross price before discounts
- GROSS_EXTENDED_AMOUNT: Gross line amount
- UOM_CODE: Unit of measure
- INTERFACE_LINE_CONTEXT: Source context (ORDER ENTRY, etc.)
- INTERFACE_LINE_ATTRIBUTE1..15: Source references
- WAREHOUSE_ID: Shipping warehouse
- SALES_ORDER: Sales order reference
- SALES_ORDER_LINE: Sales order line reference
- PREVIOUS_CUSTOMER_TRX_LINE_ID: Previous line (for credit memos)
- CREATION_DATE, CREATED_BY

AR_PAYMENT_SCHEDULES_ALL - Payment/Receipt Schedules
Purpose: Stores payment schedule and balance information
Key Columns:
- PAYMENT_SCHEDULE_ID: Schedule identifier (Primary Key)
- CUSTOMER_TRX_ID: Invoice transaction
- CASH_RECEIPT_ID: Receipt (if payment applied)
- CLASS: INV (Invoice), PMT (Payment), CM (Credit Memo), DM (Debit Memo)
- STATUS: OP (Open), CL (Closed), DISPUTE, PENDING
- DUE_DATE: Payment due date
- AMOUNT_DUE_ORIGINAL: Original amount due
- AMOUNT_DUE_REMAINING: Outstanding balance
- AMOUNT_CREDITED: Credit memo amount applied
- AMOUNT_APPLIED: Receipt amount applied
- AMOUNT_ADJUSTED: Adjustment amount
- DISCOUNT_TAKEN_EARNED, DISCOUNT_TAKEN_UNEARNED
- GL_DATE: GL date
- ORG_ID: Operating unit
- CREATION_DATE, CREATED_BY

AR_CASH_RECEIPTS_ALL - Receipt Headers
Purpose: Stores customer payment/receipt headers
Key Columns:
- CASH_RECEIPT_ID: Receipt identifier (Primary Key)
- RECEIPT_NUMBER: Receipt number
- RECEIPT_DATE: Receipt date
- AMOUNT: Receipt amount
- CURRENCY_CODE: Receipt currency
- EXCHANGE_RATE, EXCHANGE_RATE_TYPE, EXCHANGE_DATE
- PAY_FROM_CUSTOMER: Customer making payment
- STATUS: APPROVED, CLEARED, REVERSED, etc.
- TYPE: CASH, MISC
- CUSTOMER_RECEIPT_REFERENCE: Customer reference (check number, etc.)
- DEPOSIT_DATE: Bank deposit date
- COMMENTS: Receipt comments
- ORG_ID: Operating unit
- CREATION_DATE, CREATED_BY

AR_RECEIVABLE_APPLICATIONS_ALL - Receipt Applications
Purpose: Links receipts to invoices (application of payments)
Key Columns:
- RECEIVABLE_APPLICATION_ID: Application identifier (Primary Key)
- CASH_RECEIPT_ID: Receipt being applied
- APPLIED_CUSTOMER_TRX_ID: Invoice being paid
- AMOUNT_APPLIED: Amount applied to invoice
- APPLY_DATE: Application date
- GL_DATE: GL date
- STATUS: APP (Applied), UNAPP (Unapplied), ACC (On account)
- DISPLAY: Y/N (display to customer)
- EARNED_DISCOUNT_TAKEN: Discount amount
- UNEARNED_DISCOUNT_TAKEN: Unearned discount
- CREATION_DATE, CREATED_BY

HZ_CUST_ACCOUNTS - Customer Accounts
(See Order Management section for details)

HZ_CUST_SITE_USES_ALL - Customer Site Uses
(See Order Management section for details)

RA_CUST_TRX_TYPES_ALL - Transaction Types
Purpose: Defines transaction types (Invoice, Credit Memo, etc.)
Key Columns:
- CUST_TRX_TYPE_ID: Type identifier (Primary Key)
- NAME: Type name
- TYPE: INV, CM, DM (Invoice, Credit Memo, Debit Memo)
- POST_TO_GL: Y/N
- CREDIT_MEMO_TYPE_ID: Related credit memo type
- ORG_ID: Operating unit

=== COMMON AR QUERIES ===

-- =====================================================
-- Query: Customer Invoices with Balance
-- Module: Accounts Receivable
-- Tables: RA_CUSTOMER_TRX_ALL, HZ_CUST_ACCOUNTS, AR_PAYMENT_SCHEDULES_ALL
-- Parameters: :p_org_id, :p_customer_number (optional)
-- =====================================================

SELECT 
    rct.trx_number AS invoice_number,
    rct.trx_date AS invoice_date,
    hca.account_number AS customer_number,
    hca.account_name AS customer_name,
    rctt.name AS transaction_type,
    aps.amount_due_original AS invoice_amount,
    aps.amount_due_remaining AS balance,
    aps.due_date,
    TRUNC(SYSDATE - aps.due_date) AS days_overdue,
    rct.invoice_currency_code AS currency,
    aps.status
FROM ra_customer_trx_all rct
INNER JOIN hz_cust_accounts hca
    ON rct.bill_to_customer_id = hca.cust_account_id
INNER JOIN ra_cust_trx_types_all rctt
    ON rct.cust_trx_type_id = rctt.cust_trx_type_id
    AND rct.org_id = rctt.org_id
INNER JOIN ar_payment_schedules_all aps
    ON rct.customer_trx_id = aps.customer_trx_id
WHERE rct.org_id = :p_org_id
  AND hca.account_number = NVL(:p_customer_number, hca.account_number)
  AND aps.class = 'INV'
  AND aps.status = 'OP'
ORDER BY aps.due_date, hca.account_name;


-- =====================================================
-- Query: Customer Aging Report
-- Module: Accounts Receivable
-- Tables: AR_PAYMENT_SCHEDULES_ALL, RA_CUSTOMER_TRX_ALL, HZ_CUST_ACCOUNTS
-- Parameters: :p_org_id, :p_as_of_date
-- =====================================================

SELECT 
    hca.account_number,
    hca.account_name,
    SUM(aps.amount_due_remaining) AS total_outstanding,
    SUM(CASE WHEN TRUNC(:p_as_of_date - aps.due_date) <= 0 
             THEN aps.amount_due_remaining ELSE 0 END) AS current_bucket,
    SUM(CASE WHEN TRUNC(:p_as_of_date - aps.due_date) BETWEEN 1 AND 30 
             THEN aps.amount_due_remaining ELSE 0 END) AS bucket_1_30,
    SUM(CASE WHEN TRUNC(:p_as_of_date - aps.due_date) BETWEEN 31 AND 60 
             THEN aps.amount_due_remaining ELSE 0 END) AS bucket_31_60,
    SUM(CASE WHEN TRUNC(:p_as_of_date - aps.due_date) BETWEEN 61 AND 90 
             THEN aps.amount_due_remaining ELSE 0 END) AS bucket_61_90,
    SUM(CASE WHEN TRUNC(:p_as_of_date - aps.due_date) > 90 
             THEN aps.amount_due_remaining ELSE 0 END) AS bucket_over_90
FROM ar_payment_schedules_all aps
INNER JOIN ra_customer_trx_all rct
    ON aps.customer_trx_id = rct.customer_trx_id
INNER JOIN hz_cust_accounts hca
    ON rct.bill_to_customer_id = hca.cust_account_id
WHERE aps.org_id = :p_org_id
  AND aps.class = 'INV'
  AND aps.status = 'OP'
  AND aps.amount_due_remaining > 0
GROUP BY hca.account_number, hca.account_name
ORDER BY total_outstanding DESC;


-- =====================================================
-- Query: Receipt Applications
-- Module: Accounts Receivable
-- Tables: AR_CASH_RECEIPTS_ALL, AR_RECEIVABLE_APPLICATIONS_ALL, RA_CUSTOMER_TRX_ALL
-- Parameters: :p_receipt_number
-- =====================================================

SELECT 
    acr.receipt_number,
    acr.receipt_date,
    acr.amount AS receipt_amount,
    acr.currency_code,
    rct.trx_number AS invoice_number,
    ara.amount_applied,
    ara.earned_discount_taken AS discount_taken,
    ara.apply_date,
    ara.gl_date,
    ara.status AS application_status
FROM ar_cash_receipts_all acr
LEFT JOIN ar_receivable_applications_all ara
    ON acr.cash_receipt_id = ara.cash_receipt_id
LEFT JOIN ra_customer_trx_all rct
    ON ara.applied_customer_trx_id = rct.customer_trx_id
WHERE acr.receipt_number = :p_receipt_number
ORDER BY ara.apply_date;

=== QUERY WRITING BEST PRACTICES ===

1. Filter by ORG_ID for multi-org architecture
2. Use AR_PAYMENT_SCHEDULES_ALL for balance and due date information
3. Class field indicates record type: INV, PMT, CM, DM
4. Status='OP' for open/outstanding items
5. Calculate aging using (SYSDATE - DUE_DATE)
6. Link customer info via HZ_CUST_ACCOUNTS
7. For receipt applications, join via AR_RECEIVABLE_APPLICATIONS_ALL
8. AMOUNT_DUE_REMAINING shows current balance
9. Link to sales orders via INTERFACE_LINE_ATTRIBUTE fields
10. Use RA_CUST_TRX_TYPES_ALL for transaction type descriptions
";
            File.WriteAllText(filePath, content);
            _logger.LogInformation("Created Oracle EBS GL schema context");
        }
    }
}
