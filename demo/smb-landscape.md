# SMB Landscape

## Systems

- sys-crm | CRM Platform | active
  description: Customer relationship management system

- sys-ecommerce | E-Commerce | active
  description: Online storefront and order processing

## Applications

- app-checkout | Checkout Service | active
  description: Handles payment processing
  tag: tier: critical
  version: 2.1.0
  vendor: in-house
  businessOwner: CTO

- app-crm-ui | CRM Dashboard | active
  description: Web interface for CRM
  tag: tier: critical
  version: 3.0.1
  vendor: VEV
  businessOwner: VP Sales

- app-reporting | Reporting Engine | draft
  description: Business intelligence and analytics
  tag: tier: standard
  version: 1.0.0
  vendor: in-house
  businessOwner: CFO

## Servers

- srv-prod-01 | Production Server 1 | active
  hostname: prod01.internal
  environment: production
  os: Ubuntu 22.04

- srv-prod-02 | Production Server 2 | active
  hostname: prod02.internal
  environment: production
  os: Ubuntu 22.04

- srv-dev-01 | Development Server | active
  hostname: dev01.internal
  environment: development
  os: Ubuntu 22.04

## Infrastructure

- net-vpc | AWS VPC | active
  category: network
  location: eu-west-1

- db-rds | RDS Cluster | active
  category: database
  location: eu-west-1

## Data Areas

- da-customers | Customer Master Data | active
  realisation: microservice

- da-orders | Order Data | active
  realisation: monolith

- da-products | Product Catalog | active
  realisation: microservice

## Datasets

- ds-customers | Customers | active
  physical_name: dbo.customers
  owner: CRM team

- ds-orders | Orders | active
  physical_name: dbo.orders
  owner: E-Commerce team

- ds-products | Products | active
  physical_name: dbo.products
  owner: Catalog team

## Columns

- col-customer-id | customer_id | active
  data_type: uuid
  nullable: false

- col-order-id | order_id | active
  data_type: uuid
  nullable: false

- col-customer-name | customer_name | active
  data_type: varchar(255)
  nullable: true

- col-order-customer-id | customer_id | active
  data_type: uuid
  nullable: false

## Relationships

- sys-crm part-of sys-crm description: Self-reference for context
- app-checkout runs-on srv-prod-01
- app-crm-ui runs-on srv-prod-02
- app-reporting runs-on srv-dev-01
- srv-prod-01 runs-on net-vpc
- srv-prod-02 runs-on net-vpc
- srv-dev-01 runs-on net-vpc
- db-rds runs-on net-vpc
- da-customers part-of sys-crm
- da-orders part-of sys-ecommerce
- da-products part-of sys-ecommerce
- ds-customers part-of da-customers
- ds-orders part-of da-orders
- ds-products part-of da-products
- col-customer-id part-of ds-customers
- col-customer-name part-of ds-customers
- col-order-id part-of ds-orders
- col-order-customer-id part-of ds-orders
- col-product-id part-of ds-products
- col-order-customer-id joins-on col-customer-id description: Orders link to customers

## Mode

merge
