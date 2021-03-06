const express = require('express')
const next = require('next')
const { parse } = require('url')
const routes = require("./routes")
const pricing = require('./components/calculatePrice')
const { parseOneAddress } = require("email-addresses")

const dev = process.env.NODE_ENV !== 'production'
const stripeKey = process.env.LOGARY_STRIPE_SECRET_KEY
if (stripeKey == null) throw new Error("Missing env var LOGARY_STRIPE_SECRET_KEY")
const stripe = require("stripe")(stripeKey)
stripe.setApiVersion('2019-03-14');
const app = next({ dev })
const handle = app.getRequestHandler()

const remap = actualPath => {
  if (routes.hasOwnProperty(actualPath)) {
    console.log(`Mapping ${actualPath} => ${routes[actualPath]}`)
    return [ true, routes[actualPath] ]
  } else {
    return [ false, null  ];
  }
}

/**
 * https://stripe.com/docs/billing/subscriptions/payment
 * @param {*} token Stripe Source id or Token https://stripe.com/docs/api#tokens https://stripe.com/docs/api#sources
 * @param {companyName, name, email, vatNo?} customer
 * @param {address,name,local,domain,parts} parsedEmail
 * @returns https://stripe.com/docs/api/customers/create
 */
const getOrCreateCustomer = async (token, customer, parsedEmail) => {
  console.debug("Listing customers based on the e-mail provided.")
  const res = await stripe.customers.list({ limit: 1, email: parsedEmail.address })
  if (res.data.length > 0) {
    const existing = res.data[0]
    if (token === existing.default_source) {
      console.info(`Found existing customer with id ${existing.id}, reusing as tokens were identical.`)
      return existing;
    }

    console.info(`Found existing customer with id ${existing.id}, updating source.`)
    return await stripe.customers.update(existing.id, {
      source: token,
      metadata: {
        ...existing.metadata,
        emailName: parsedEmail.name,
        companyName: customer.companyName.trim(),
        updated: Date.now(),
      },
    })
  }

  console.info("Creating new customer.")
  return await stripe.customers.create({
    name: customer.name.trim(),
    email: parsedEmail.address,
    description: `Customer for "${customer.name.trim()}" <${parsedEmail.address}>`,
    metadata: {
      emailName: parsedEmail.name,
      companyName: customer.companyName.trim(),
      created: Date.now(),
    },
    source: token
  });
}

const stringly = price =>
  Object
    .keys(price)
    .map(k => [ k, typeof price[k] === 'object' ? pricing.formatMoney(price[k]) : String(price[k])])
    .reduce((acc, [ k, v ]) => ({ ...acc, [k]: v }), {})

const getProducts = async () =>  {
  const ps = await stripe.products.list({
    active: true,
    limit: 10,
    type: 'service'
  })

  if (ps.data.length !== 2) {
    throw new Error("Expected two active=true, type=service products")
  }

  return {
    cores: ps.data.filter(x => x.name === 'logary_license_cores')[0],
    devs: ps.data.filter(x => x.name === "logary_license_devs")[0]
  }
}

/**
 * https://stripe.com/docs/api/subscriptions/create
 * @param {Stripe.Customer} customer
 */
const createSubscription = async (cores, devs, price, customer) => {
  // e.g. ("logary_devs_5", "logary_devs_1") => ...
  const sortLatestSuffix = (x, y) => /_(\d{1,})/.exec(y)[1] - /_(\d{1,})/.exec(x)[1]
  const products = await getProducts();
  const cs = await stripe.plans.list({ limit: 50, product: products.cores.id })
  const ds = await stripe.plans.list({ limit: 50, product: products.devs.id })
  console.log('Got cs back', cs)
  console.log('Got ds back', ds)
  const planCore = [ ...cs.data ].sort(sortLatestSuffix)[0]
  const planDev = [ ...ds.data ].sort(sortLatestSuffix)[0]
  const s = await stripe.subscriptions.create({
    customer: customer.id,
    items: [
      { plan: planDev.id, quantity: devs }, // devs 1 test
      { plan: planCore.id, quantity: cores } // cores 1 test
    ],
    billing: "charge_automatically",
    metadata: {
      ...stringly(price)
    },
    expand: [
      "latest_invoice.payment_intent"
    ],
    tax_percent: 100 * price.vatRate
  })
  console.log('Got subscription back', s)
  // TODO: manually attach https://stripe.com/docs/billing/subscriptions/discounts to the subscription
  // after creation to match the money off
  return s;
}

const charge = async (req, res) => {
  try {
    // validate price
    const chargeVAT = req.body.customer.vatNo == null || req.body.customer.vatNo === "",
          ep = pricing.calculatePrice(req.body.cores, req.body.devs, req.body.years, pricing.ContinuousRebate, chargeVAT)

    console.log("Calculated price", ep)

    if (!pricing.equal(ep.total, req.body.price.total)) {
      logger.error("Received value from client", rq.body.price, "but calculated it as", ep)
      res.status(400).statusMessage("Bad amount or not same currency");
      return
    }

    // validate e-mail
    // https://www.npmjs.com/package/email-addresses#obj--addrsparseoneaddressopts
    const email = parseOneAddress({ input: req.body.customer.email, rejectTLD: true, simple: false });
    if (email == null) {
      res.status(400).statusMessage("Bad e-mail");
      return
    }

    // validate company name
    if (req.body.customer.companyName == null || req.body.customer.companyName.trim().length === 0) {
      res.status(400).statusMessage("Bad company name");
      return
    }

    const customer = await getOrCreateCustomer(req.body.token.id, req.body.customer, email);
    const subscription = await createSubscription(req.body.cores, req.body.devs, ep, customer);
    const outcome = `${subscription.status}|${(subscription.latest_invoice.payment_intent || {}).status}`;
    console.info(`Outcome for ${customer.id}: ${outcome}`)
    // https://stripe.com/docs/billing/subscriptions/payment#initial-charge
    // adapted https://jsonapi.org/format/#error-objects
    switch (outcome) {
      case "active|succeeded": // Outcome 1: Payment succeeds
        res.json({
          type: 'success',
          payload: {
            code: "active|succeeded",
            title: "Thanks!"
          }
        })
        break

      case "incomplete|requires_payment_method": // Outcome 3: Payment fails
        res.json({
          type: 'failure',
          payload: {
            code: "incomplete|requires_payment_method",
            title: "Payment failed"
          }
        })
        break

      case "trialing|": // Outcome 2: Trial starts (we don't have!)
      default:
        throw new Error("Impl error: outcome: " + outcome)
    }
  } catch (err) {
    console.error(err)
    res.status(500).end();
  }
}

app
  .prepare()
  .then(() => {
    const server = express()
    server.use(require("body-parser").text())
    server.use(require("body-parser").json())
    const port = process.env.PORT || 3000

    server.get('*', (req, res) => {
      const parsedUrl = parse(req.url, true)
      const { pathname, query } = parsedUrl
      const [ shouldRemap, newPath ] = remap(pathname);
      if (shouldRemap) {
        return app.render(req, res, newPath, query)
      } else {
        // console.log('got parsed url ', parsedUrl)
        return handle(req, res, parsedUrl);
      }
    })

    server.post("/charge", charge);

    server.listen(port, err => {
      if (err) throw err
      console.info(`> Ready on http://localhost:${port}`)
    })
  })
  .catch(ex => {
    console.error(ex.stack)
    process.exit(1)
  })